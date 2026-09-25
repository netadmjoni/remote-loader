using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class WgbPollingServiceTests
{
    [Fact]
    public async Task PollingServiceUsesFakeClientAndPublishesAssociationUpdate()
    {
        var client = new FakeWgbCommandClient([
            "Parent AP Name: AP-A\r\nRSSI: -61\r\nAssociation status: Associated"
        ]);
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(client, cancellation);

        var task = service.RunAsync(CreateOptions(), sink.AddAsync, cancellation.Token);

        var update = await sink.WaitForEventAsync(WgbPollEventKind.AssociationUpdated);
        cancellation.Cancel();
        await task;

        Assert.Equal("AP-A", update.Association?.ParentApName);
        Assert.Equal("Associated", update.Association?.AssociationStatus);
        Assert.True(client.CallCount >= 1);
    }

    [Fact]
    public async Task PollingServicePublishesParentApChangedWithOldAndNewValue()
    {
        var client = new FakeWgbCommandClient([
            "Parent AP Name: AP-A",
            "Parent AP Name: AP-B"
        ]);
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(client, cancellation);

        var task = service.RunAsync(CreateOptions(), sink.AddAsync, cancellation.Token);

        var changed = await sink.WaitForEventAsync(WgbPollEventKind.ParentApChanged);
        cancellation.Cancel();
        await task;

        Assert.Equal("AP-A", changed.OldParentApName);
        Assert.Equal("AP-B", changed.NewParentApName);
        Assert.NotEqual(default, changed.Timestamp);
    }

    [Fact]
    public async Task PollingServicePublishesPollFailedWhenFakeClientThrows()
    {
        var client = new FakeWgbCommandClient([new WgbCommandException("fake failure")]);
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(client, cancellation);

        var task = service.RunAsync(CreateOptions(), sink.AddAsync, cancellation.Token);

        var failed = await sink.WaitForEventAsync(WgbPollEventKind.PollFailed);
        cancellation.Cancel();
        await task;

        Assert.Contains("fake failure", failed.Message);
    }

    [Fact]
    public async Task PollingServiceDoesNotStartOverlappingPolls()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeWgbCommandClient([
            async (CancellationToken cancellationToken) =>
            {
                await gate.Task.WaitAsync(cancellationToken);
                return "Parent AP Name: AP-A";
            },
            "Parent AP Name: AP-B"
        ]);
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(client, cancellation);

        var task = service.RunAsync(CreateOptions(), sink.AddAsync, cancellation.Token);
        await client.WaitForCallCountAsync(1);
        await Task.Delay(100);

        Assert.Equal(1, client.CallCount);
        Assert.Equal(1, client.MaxConcurrentCalls);

        gate.SetResult();
        await sink.WaitForEventCountAsync(WgbPollEventKind.AssociationUpdated, 2);
        cancellation.Cancel();
        await task;
        Assert.Equal(1, client.MaxConcurrentCalls);
    }

    [Fact]
    public async Task UsefulOutputWithWarningStillPublishesAssociationUpdate()
    {
        var diagnostics = new WgbCommandExecutionDiagnostics(
            ConnectionSucceeded: true,
            EnableAttempted: true,
            EnableSucceeded: true,
            CommandExecuted: true,
            FinalPromptConfirmed: false,
            PromptResyncAttempted: true,
            PromptResyncSucceeded: true,
            Warning: "Command output received but final prompt not confirmed.",
            FailureReason: null,
            Events:
            [
                new WgbCommandDiagnosticEvent(WgbPollEventKind.CommandWarning, DateTimeOffset.UtcNow, "Command output received but final prompt not confirmed.")
            ]);
        var client = new FakeWgbCommandClient([
            new WgbCommandExecutionResult("AP=MGN1080STV-223\r\nRSSI=45\r\nchannel=11", diagnostics)
        ]);
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(client, cancellation);

        var task = service.RunAsync(CreateOptions(parserProfile: WgbParserProfiles.GenericKeyValue), sink.AddAsync, cancellation.Token);

        var updated = await sink.WaitForEventAsync(WgbPollEventKind.AssociationUpdated);
        var succeeded = await sink.WaitForEventAsync(WgbPollEventKind.PollSucceeded);
        cancellation.Cancel();
        await task;

        Assert.Equal("MGN1080STV-223", updated.Association?.ParentApName);
        Assert.Equal("Command output received but final prompt not confirmed.", succeeded.Message);
        Assert.Contains(sink.Events, item => item.Kind == WgbPollEventKind.CommandWarning);
    }

    [Fact]
    public async Task OfflineAtStartKeepsRetryingAndConnectsLater()
    {
        var client = new FakeWgbCommandClient([
            new WgbCommandException("connect timed out"),
            "Parent AP Name: AP-A"
        ]);
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(client, cancellation);

        var task = service.RunAsync(CreateOptions(), sink.AddAsync, cancellation.Token);

        await sink.WaitForEventAsync(WgbPollEventKind.PollFailed);
        var updated = await sink.WaitForEventAsync(WgbPollEventKind.AssociationUpdated);
        cancellation.Cancel();
        await task;

        Assert.Equal("AP-A", updated.Association?.ParentApName);
        Assert.Contains(sink.Events, item => item.Kind == WgbPollEventKind.Connecting);
        Assert.Contains(sink.Events, item => item.Kind == WgbPollEventKind.ReconnectScheduled);
        Assert.Contains(sink.Events, item => item.Kind == WgbPollEventKind.Connected);
    }

    [Fact]
    public async Task DeviceDisappearAndReturnPublishesSessionLostDisconnectedAndConnected()
    {
        var client = new FakeWgbCommandClient([
            "Parent AP Name: AP-A",
            new WgbCommandException("session lost"),
            "Parent AP Name: AP-A"
        ]);
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(client, cancellation);

        var task = service.RunAsync(CreateOptions(), sink.AddAsync, cancellation.Token);

        await sink.WaitForEventCountAsync(WgbPollEventKind.AssociationUpdated, 2);
        cancellation.Cancel();
        await task;

        Assert.Contains(sink.Events, item => item.Kind == WgbPollEventKind.SessionLost);
        Assert.Contains(sink.Events, item => item.Kind == WgbPollEventKind.Disconnected && item.Message == "session lost");
        Assert.True(sink.Events.Count(item => item.Kind == WgbPollEventKind.Connected) >= 2);
    }

    [Fact]
    public async Task ReconnectBackoffResetsAfterSuccessfulPoll()
    {
        var client = new FakeWgbCommandClient([
            new WgbCommandException("offline"),
            new WgbCommandException("offline"),
            "Parent AP Name: AP-A",
            new WgbCommandException("offline")
        ]);
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();
        var delay = new RecordingDelay(cancellation, cancelAfterDelayCount: 4);
        var service = new WgbPollingService(client, new WgbAssociationParser(), delay.DelayAsync);

        await service.RunAsync(CreateOptions(), sink.AddAsync, cancellation.Token);

        var reconnectDelays = delay.Delays.Where(delay => delay.TotalSeconds is not 1).ToArray();
        Assert.Equal(new[] { 2d, 5d, 2d }, reconnectDelays.Select(delay => delay.TotalSeconds).ToArray());
    }

    [Fact]
    public async Task StopCancelsReconnectLoopAndDisposesPersistentSession()
    {
        var client = new FakeWgbCommandClient([new WgbCommandException("offline")]);
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();
        var reconnectDelayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new WgbPollingService(
            client,
            new WgbAssociationParser(),
            async (delay, cancellationToken) =>
            {
                if (delay.TotalSeconds != 1)
                {
                    reconnectDelayStarted.SetResult();
                }

                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            });

        var task = service.RunAsync(CreateOptions(), sink.AddAsync, cancellation.Token);
        await reconnectDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await task;

        Assert.True(client.ResetSessionCalled);
    }

    [Fact]
    public async Task RepetitivePollFailuresAreRateLimited()
    {
        var client = new FakeWgbCommandClient([
            new WgbCommandException("offline"),
            new WgbCommandException("offline"),
            new WgbCommandException("offline")
        ]);
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();
        var delay = new RecordingDelay(cancellation, cancelAfterDelayCount: 3);
        var service = new WgbPollingService(client, new WgbAssociationParser(), delay.DelayAsync);

        await service.RunAsync(CreateOptions(failureRateLimitSeconds: 60), sink.AddAsync, cancellation.Token);

        Assert.Single(sink.Events.Where(item => item.Kind == WgbPollEventKind.PollFailed));
        Assert.Equal(3, sink.Events.Count(item => item.Kind == WgbPollEventKind.ReconnectScheduled));
    }

    private static WgbPollingService CreateService(
        FakeWgbCommandClient client,
        CancellationTokenSource cancellation)
    {
        var delay = new RecordingDelay(cancellation);
        return new WgbPollingService(client, new WgbAssociationParser(), delay.DelayAsync);
    }

    private static WgbPollingOptions CreateOptions(
        string parserProfile = WgbParserProfiles.Iw9167WgbV1,
        int failureRateLimitSeconds = 30)
    {
        return new WgbPollingOptions(
            "192.0.2.10",
            22,
            "user",
            "password",
            "show wgb dot11 associations",
            parserProfile,
            PollIntervalSeconds: 1,
            CommandTimeoutMilliseconds: 1000,
            ReconnectInitialSeconds: 2,
            ReconnectMaximumSeconds: 60,
            StaleAfterSeconds: 5,
            FailureEventRateLimitSeconds: failureRateLimitSeconds);
    }

    private sealed class FakeWgbCommandClient : IWgbCommandClient, IWgbPersistentCommandClient
    {
        private readonly Queue<object> _responses;
        private readonly object _sync = new();
        private int _activeCalls;

        public FakeWgbCommandClient(IEnumerable<object> responses)
        {
            _responses = new Queue<object>(responses);
        }

        public int CallCount { get; private set; }

        public int MaxConcurrentCalls { get; private set; }

        public bool ResetSessionCalled { get; private set; }

        public async Task<string> ExecuteCommandAsync(
            WgbCommandRequest request,
            CancellationToken cancellationToken)
        {
            var result = await ExecuteCommandWithDiagnosticsAsync(request, cancellationToken);
            return result.RawOutput;
        }

        public async Task<WgbCommandExecutionResult> ExecuteCommandWithDiagnosticsAsync(
            WgbCommandRequest request,
            CancellationToken cancellationToken)
        {
            object response;
            lock (_sync)
            {
                CallCount++;
                _activeCalls++;
                MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, _activeCalls);
                response = _responses.Count == 0
                    ? ""
                    : _responses.Dequeue();
            }

            try
            {
                if (response is Func<CancellationToken, Task<string>> asyncString)
                {
                    response = await asyncString(cancellationToken);
                }

                if (response is Func<CancellationToken, Task<object>> asyncObject)
                {
                    response = await asyncObject(cancellationToken);
                }

                if (response is Exception ex)
                {
                    throw ex;
                }

                if (response is WgbCommandExecutionResult result)
                {
                    return result;
                }

                return new WgbCommandExecutionResult(
                    (string)response,
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
            finally
            {
                lock (_sync)
                {
                    _activeCalls--;
                }
            }
        }

        public Task ResetSessionAsync(CancellationToken cancellationToken)
        {
            ResetSessionCalled = true;
            return Task.CompletedTask;
        }

        public async Task WaitForCallCountAsync(int expected)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (CallCount >= expected)
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Expected {expected} command calls, but saw {CallCount}.");
        }
    }

    private sealed class RecordingDelay
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly int? _cancelAfterDelayCount;

        public RecordingDelay(
            CancellationTokenSource cancellation,
            int? cancelAfterDelayCount = null)
        {
            _cancellation = cancellation;
            _cancelAfterDelayCount = cancelAfterDelayCount;
        }

        public List<TimeSpan> Delays { get; } = [];

        public async Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            if (_cancelAfterDelayCount is not null && Delays.Count >= _cancelAfterDelayCount.Value)
            {
                _cancellation.Cancel();
            }

            await Task.Yield();
        }
    }

    private sealed class WgbPollEventSink
    {
        private readonly List<WgbPollEvent> _events = [];

        public IReadOnlyList<WgbPollEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public ValueTask AddAsync(WgbPollEvent pollEvent)
        {
            lock (_events)
            {
                _events.Add(pollEvent);
            }

            return ValueTask.CompletedTask;
        }

        public async Task<WgbPollEvent> WaitForEventAsync(WgbPollEventKind kind)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);

            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (_events)
                {
                    var match = _events.FirstOrDefault(pollEvent => pollEvent.Kind == kind);
                    if (match is not null)
                    {
                        return match;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Expected WGB poll event was not published: {kind}.");
        }

        public async Task WaitForEventCountAsync(WgbPollEventKind kind, int expectedCount)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);

            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (_events)
                {
                    if (_events.Count(pollEvent => pollEvent.Kind == kind) >= expectedCount)
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Expected {expectedCount} WGB poll events were not published: {kind}.");
        }
    }
}
