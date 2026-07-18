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
        var service = new WgbPollingService(client, new WgbAssociationParser());
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();

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
        var service = new WgbPollingService(client, new WgbAssociationParser());
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();

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
        var service = new WgbPollingService(client, new WgbAssociationParser());
        var sink = new WgbPollEventSink();
        using var cancellation = new CancellationTokenSource();

        var task = service.RunAsync(CreateOptions(), sink.AddAsync, cancellation.Token);

        var failed = await sink.WaitForEventAsync(WgbPollEventKind.PollFailed);
        cancellation.Cancel();
        await task;

        Assert.Contains("fake failure", failed.Message);
    }

    private static WgbPollingOptions CreateOptions()
    {
        return new WgbPollingOptions(
            "192.0.2.10",
            22,
            "user",
            "password",
            "show wgb dot11 associations",
            WgbParserProfiles.Iw9167WgbV1,
            PollIntervalSeconds: 1,
            CommandTimeoutMilliseconds: 1000);
    }

    private sealed class FakeWgbCommandClient : IWgbCommandClient
    {
        private readonly Queue<object> _responses;

        public FakeWgbCommandClient(IEnumerable<object> responses)
        {
            _responses = new Queue<object>(responses);
        }

        public int CallCount { get; private set; }

        public Task<string> ExecuteCommandAsync(
            WgbCommandRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;

            if (_responses.Count == 0)
            {
                return Task.FromResult("");
            }

            var response = _responses.Dequeue();
            if (response is Exception ex)
            {
                return Task.FromException<string>(ex);
            }

            return Task.FromResult((string)response);
        }
    }

    private sealed class WgbPollEventSink
    {
        private readonly List<WgbPollEvent> _events = [];

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
    }
}
