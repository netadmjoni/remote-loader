namespace WgbDiagnostics.Core.Wgb;

public sealed class WgbPollingService : IWgbPollingService
{
    private readonly IWgbCommandClient _commandClient;
    private readonly IWgbAssociationParser _parser;

    public WgbPollingService(
        IWgbCommandClient commandClient,
        IWgbAssociationParser parser)
    {
        _commandClient = commandClient;
        _parser = parser;
    }

    public async Task RunAsync(
        WgbPollingOptions options,
        Func<WgbPollEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        var isConnected = false;
        WgbAssociationSnapshot? previousAssociation = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var rawOutput = await _commandClient.ExecuteCommandAsync(
                    options.ToCommandRequest(),
                    cancellationToken);
                var parseResult = _parser.Parse(rawOutput, options.ParserProfile);
                var association = parseResult.Association;
                var timestamp = DateTimeOffset.UtcNow;

                if (!isConnected)
                {
                    isConnected = true;
                    await onEvent(CreateEvent(WgbPollEventKind.Connected, timestamp, association, parseResult, rawOutput));
                }

                await onEvent(CreateEvent(WgbPollEventKind.PollSucceeded, timestamp, association, parseResult, rawOutput));
                await onEvent(CreateEvent(WgbPollEventKind.AssociationUpdated, timestamp, association, parseResult, rawOutput));

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
                            rawOutput,
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
                            PotentialBugMatchId: null));
                    }
                }

                previousAssociation = association;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var timestamp = DateTimeOffset.UtcNow;
                if (isConnected)
                {
                    isConnected = false;
                    await onEvent(CreateEvent(WgbPollEventKind.Disconnected, timestamp, previousAssociation, parseResult: null, rawOutput: null, ex.Message));
                }

                await onEvent(CreateEvent(WgbPollEventKind.PollFailed, timestamp, previousAssociation, parseResult: null, rawOutput: null, ex.Message));
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds)),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
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
}
