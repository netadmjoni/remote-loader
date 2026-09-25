namespace WgbDiagnostics.Core.Wgb;

public sealed class WgbPollingService : IWgbPollingService
{
    private static readonly int[] ReconnectBackoffSeconds = [2, 5, 10, 30];

    private readonly IWgbCommandClient _commandClient;
    private readonly IWgbAssociationParser _parser;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public WgbPollingService(
        IWgbCommandClient commandClient,
        IWgbAssociationParser parser)
        : this(commandClient, parser, (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    public WgbPollingService(
        IWgbCommandClient commandClient,
        IWgbAssociationParser parser,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _commandClient = commandClient;
        _parser = parser;
        _delayAsync = delayAsync;
    }

    public async Task RunAsync(
        WgbPollingOptions options,
        Func<WgbPollEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        var isConnected = false;
        WgbAssociationSnapshot? previousAssociation = null;
        var reconnectBackoff = new ReconnectBackoff(options.ReconnectInitialSeconds, options.ReconnectMaximumSeconds);
        var failureLimiter = new RepetitiveFailureLimiter(
            TimeSpan.FromSeconds(Math.Max(1, options.FailureEventRateLimitSeconds)));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!isConnected)
                {
                    await onEvent(CreateEvent(
                        WgbPollEventKind.Connecting,
                        DateTimeOffset.UtcNow,
                        previousAssociation,
                        parseResult: null,
                        rawOutput: null,
                        "Connecting to WGB SSH."));
                }

                try
                {
                    var result = await ExecuteCommandAsync(options, cancellationToken);
                    var parseResult = _parser.Parse(result.RawOutput, options.ParserProfile);
                    var association = parseResult.Association;
                    var timestamp = DateTimeOffset.UtcNow;

                    await PublishDiagnosticEventsAsync(result.Diagnostics, onEvent, association, parseResult, result.RawOutput);
                    foreach (var warning in parseResult.Warnings)
                    {
                        await onEvent(CreateEvent(
                            WgbPollEventKind.CommandWarning,
                            timestamp,
                            association,
                            parseResult,
                            result.RawOutput,
                            warning));
                    }

                    if (!isConnected)
                    {
                        isConnected = true;
                        reconnectBackoff.Reset();
                        failureLimiter.Reset();
                        await onEvent(CreateEvent(WgbPollEventKind.Connected, timestamp, association, parseResult, result.RawOutput));
                    }
                    else
                    {
                        reconnectBackoff.Reset();
                        failureLimiter.Reset();
                    }

                    await onEvent(CreateEvent(WgbPollEventKind.PollSucceeded, timestamp, association, parseResult, result.RawOutput, result.Diagnostics.Warning));
                    await onEvent(CreateEvent(WgbPollEventKind.AssociationUpdated, timestamp, association, parseResult, result.RawOutput));

                    if (previousAssociation is not null)
                    {
                        var classification = WgbRoamClassifier.Classify(previousAssociation, association);
                        var parentChanged = !StringComparer.OrdinalIgnoreCase.Equals(
                            previousAssociation.ParentApName,
                            association.ParentApName);

                        if (parentChanged || classification != WgbRoamClassification.Unknown)
                        {
                            await onEvent(new WgbPollEvent(
                                WgbPollEventKind.ParentApChanged,
                                timestamp,
                                association,
                                parseResult,
                                result.RawOutput,
                                "Parent AP transition observed.",
                                previousAssociation.ParentApName,
                                association.ParentApName,
                                previousAssociation.ParentBssid,
                                association.ParentBssid,
                                previousAssociation.Channel,
                                association.Channel,
                                previousAssociation.RadioId,
                                association.RadioId,
                                classification,
                                previousAssociation.Rssi,
                                association.Rssi,
                                PotentialBugMatchId: null));
                        }
                    }

                    previousAssociation = association;
                    await DelayAsync(TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds)), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var timestamp = DateTimeOffset.UtcNow;
                    var diagnostics = ex is WgbCommandException commandException
                        ? commandException.Diagnostics
                        : null;
                    var failureReason = diagnostics?.FailureReason ?? ex.Message;

                    if (diagnostics is not null)
                    {
                        await PublishDiagnosticEventsAsync(diagnostics, onEvent, previousAssociation, parseResult: null, rawOutput: null);
                    }

                    if (isConnected)
                    {
                        isConnected = false;
                        await onEvent(CreateEvent(WgbPollEventKind.SessionLost, timestamp, previousAssociation, parseResult: null, rawOutput: null, failureReason));
                        await onEvent(CreateEvent(WgbPollEventKind.Disconnected, timestamp, previousAssociation, parseResult: null, rawOutput: null, failureReason));
                    }

                    if (failureLimiter.ShouldEmit(failureReason, timestamp, out var message))
                    {
                        await onEvent(CreateEvent(WgbPollEventKind.PollFailed, timestamp, previousAssociation, parseResult: null, rawOutput: null, message));
                    }

                    var reconnectDelay = reconnectBackoff.NextDelay();
                    await onEvent(CreateEvent(
                        WgbPollEventKind.ReconnectScheduled,
                        timestamp,
                        previousAssociation,
                        parseResult: null,
                        rawOutput: null,
                        $"Reconnect scheduled in {reconnectDelay.TotalSeconds:0} seconds."));
                    try
                    {
                        await DelayAsync(reconnectDelay, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            if (_commandClient is IWgbPersistentCommandClient persistentCommandClient)
            {
                await persistentCommandClient.ResetSessionAsync(CancellationToken.None);
            }

            if (isConnected)
            {
                await onEvent(CreateEvent(
                    WgbPollEventKind.Disconnected,
                    DateTimeOffset.UtcNow,
                    previousAssociation,
                    parseResult: null,
                    rawOutput: null,
                    "WGB polling stopped."));
            }
        }
    }

    private Task<WgbCommandExecutionResult> ExecuteCommandAsync(
        WgbPollingOptions options,
        CancellationToken cancellationToken)
    {
        var request = options.ToCommandRequest();
        if (_commandClient is IWgbCommandDiagnosticsClient diagnosticsClient)
        {
            return diagnosticsClient.ExecuteCommandWithDiagnosticsAsync(request, cancellationToken);
        }

        return ExecuteWithoutDiagnosticsAsync(request, cancellationToken);
    }

    private async Task<WgbCommandExecutionResult> ExecuteWithoutDiagnosticsAsync(
        WgbCommandRequest request,
        CancellationToken cancellationToken)
    {
        var rawOutput = await _commandClient.ExecuteCommandAsync(request, cancellationToken);
        return new WgbCommandExecutionResult(
            rawOutput,
            new WgbCommandExecutionDiagnostics(
                ConnectionSucceeded: true,
                EnableAttempted: request.UseEnableMode,
                EnableSucceeded: !request.UseEnableMode,
                CommandExecuted: true,
                FinalPromptConfirmed: true,
                PromptResyncAttempted: false,
                PromptResyncSucceeded: false,
                Warning: null,
                FailureReason: null,
                Events: []));
    }

    private static async ValueTask PublishDiagnosticEventsAsync(
        WgbCommandExecutionDiagnostics diagnostics,
        Func<WgbPollEvent, ValueTask> onEvent,
        WgbAssociationSnapshot? association,
        WgbAssociationParseResult? parseResult,
        string? rawOutput)
    {
        foreach (var diagnosticEvent in diagnostics.Events)
        {
            await onEvent(CreateEvent(
                diagnosticEvent.Kind,
                diagnosticEvent.Timestamp,
                association,
                parseResult,
                rawOutput,
                diagnosticEvent.Message));
        }
    }

    private async Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await _delayAsync(delay, cancellationToken);
    }

    private static WgbPollEvent CreateEvent(
        WgbPollEventKind kind,
        DateTimeOffset timestamp,
        WgbAssociationSnapshot? association,
        WgbAssociationParseResult? parseResult,
        string? rawOutput,
        string? message = null)
    {
        return new WgbPollEvent(
            kind,
            timestamp,
            association,
            parseResult,
            rawOutput,
            message);
    }

    private sealed class ReconnectBackoff
    {
        private readonly int _initialSeconds;
        private readonly int _maximumSeconds;
        private int _failureCount;

        public ReconnectBackoff(
            int initialSeconds,
            int maximumSeconds)
        {
            _initialSeconds = Math.Max(1, initialSeconds);
            _maximumSeconds = Math.Max(_initialSeconds, maximumSeconds);
        }

        public TimeSpan NextDelay()
        {
            var candidateSeconds = _failureCount < ReconnectBackoffSeconds.Length
                ? ReconnectBackoffSeconds[_failureCount]
                : _maximumSeconds;
            _failureCount++;
            return TimeSpan.FromSeconds(Math.Clamp(candidateSeconds, _initialSeconds, _maximumSeconds));
        }

        public void Reset()
        {
            _failureCount = 0;
        }
    }

    private sealed class RepetitiveFailureLimiter
    {
        private readonly TimeSpan _minimumInterval;
        private string? _lastMessage;
        private DateTimeOffset? _lastEmittedAt;
        private int _suppressedCount;

        public RepetitiveFailureLimiter(TimeSpan minimumInterval)
        {
            _minimumInterval = minimumInterval;
        }

        public bool ShouldEmit(
            string message,
            DateTimeOffset timestamp,
            out string emittedMessage)
        {
            emittedMessage = message;
            if (!StringComparer.Ordinal.Equals(message, _lastMessage))
            {
                _lastMessage = message;
                _lastEmittedAt = timestamp;
                _suppressedCount = 0;
                return true;
            }

            if (_lastEmittedAt is null || timestamp - _lastEmittedAt.Value >= _minimumInterval)
            {
                emittedMessage = _suppressedCount > 0
                    ? $"{message} Repeated {_suppressedCount} similar failure(s) were suppressed."
                    : message;
                _lastEmittedAt = timestamp;
                _suppressedCount = 0;
                return true;
            }

            _suppressedCount++;
            return false;
        }

        public void Reset()
        {
            _lastMessage = null;
            _lastEmittedAt = null;
            _suppressedCount = 0;
        }
    }
}
