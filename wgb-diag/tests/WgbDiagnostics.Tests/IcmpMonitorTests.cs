using System.Collections.Concurrent;
using WgbDiagnostics.Core.Monitoring;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class IcmpMonitorTests
{
    [Fact]
    public async Task FakeProbeSuccessPublishesPingReply()
    {
        var probe = new FakeIcmpProbe();
        var sink = new MonitorEventSink();

        await using var run = StartMonitor(probe, sink);
        var call = await probe.WaitForCallAsync(sequenceNumber: 1);
        call.CompleteSuccess(roundTripTimeMilliseconds: 42);

        var monitorEvent = await sink.WaitForEventAsync(IcmpMonitorEventKind.PingReply);

        Assert.Equal(1, monitorEvent.SequenceNumber);
        Assert.Equal(TimeSpan.FromMilliseconds(42), monitorEvent.RoundTripTime);
        Assert.Equal(0, monitorEvent.ConsecutiveLoss);
    }

    [Fact]
    public async Task FakeProbeTimeoutStartsLoss()
    {
        var probe = new FakeIcmpProbe();
        var sink = new MonitorEventSink();

        await using var run = StartMonitor(probe, sink);
        var call = await probe.WaitForCallAsync(sequenceNumber: 1);
        call.CompleteTimeout();

        var monitorEvent = await sink.WaitForEventAsync(IcmpMonitorEventKind.LossStarted);

        Assert.Equal(1, monitorEvent.SequenceNumber);
        Assert.Equal(1, monitorEvent.ConsecutiveLoss);
        Assert.Contains("timeout", monitorEvent.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FakeProbeDelayedReplyStillPublishesSuccess()
    {
        var probe = new FakeIcmpProbe();
        var sink = new MonitorEventSink();

        await using var run = StartMonitor(probe, sink, intervalMilliseconds: 50);
        var call = await probe.WaitForCallAsync(sequenceNumber: 1);
        await Task.Delay(75);
        call.CompleteSuccess(roundTripTimeMilliseconds: 75);

        var monitorEvent = await sink.WaitForEventAsync(IcmpMonitorEventKind.PingReply);

        Assert.Equal(1, monitorEvent.SequenceNumber);
        Assert.Equal(TimeSpan.FromMilliseconds(75), monitorEvent.RoundTripTime);
    }

    [Fact]
    public async Task FakeProbeCanOverlapWhenTimeoutIsLongerThanInterval()
    {
        var probe = new FakeIcmpProbe();
        var sink = new MonitorEventSink();

        await using var run = StartMonitor(
            probe,
            sink,
            intervalMilliseconds: 20,
            timeoutMilliseconds: 1000);

        var first = await probe.WaitForCallAsync(sequenceNumber: 1);
        var second = await probe.WaitForCallAsync(sequenceNumber: 2);

        Assert.True(probe.CallCount >= 2);

        first.CompleteSuccess(roundTripTimeMilliseconds: 30);
        second.CompleteSuccess(roundTripTimeMilliseconds: 20);

        await sink.WaitForEventCountAsync(IcmpMonitorEventKind.PingReply, expectedCount: 2);
    }

    [Fact]
    public async Task FakeProbeOutOfOrderCompletionKeepsOriginalSequenceNumbers()
    {
        var probe = new FakeIcmpProbe();
        var sink = new MonitorEventSink();

        await using var run = StartMonitor(probe, sink, intervalMilliseconds: 20);

        var first = await probe.WaitForCallAsync(sequenceNumber: 1);
        var second = await probe.WaitForCallAsync(sequenceNumber: 2);

        second.CompleteSuccess(roundTripTimeMilliseconds: 20);
        first.CompleteSuccess(roundTripTimeMilliseconds: 40);

        await sink.WaitForEventCountAsync(IcmpMonitorEventKind.PingReply, expectedCount: 2);
        var pingReplies = sink.Events
            .Where(monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.PingReply)
            .ToArray();
        var sequences = pingReplies.Select(monitorEvent => monitorEvent.SequenceNumber).ToArray();

        Assert.Equal(2L, sequences[0]);
        Assert.Equal(1L, sequences[1]);
    }

    private static MonitorRun StartMonitor(
        FakeIcmpProbe probe,
        MonitorEventSink sink,
        int intervalMilliseconds = 100,
        int timeoutMilliseconds = 1000,
        int lossThresholdMilliseconds = 600)
    {
        var monitor = new IcmpMonitor(probe);
        var cancellation = new CancellationTokenSource();
        var options = new IcmpMonitorOptions(
            "example.invalid",
            intervalMilliseconds,
            timeoutMilliseconds,
            lossThresholdMilliseconds);
        var task = monitor.RunAsync(options, sink.AddAsync, cancellation.Token);

        return new MonitorRun(cancellation, task);
    }

    private sealed class FakeIcmpProbe : IIcmpProbe
    {
        private readonly object _sync = new();
        private readonly List<ProbeCall> _calls = [];

        public int CallCount
        {
            get
            {
                lock (_sync)
                {
                    return _calls.Count;
                }
            }
        }

        public Task<IcmpProbeResponse> SendAsync(
            IcmpProbeRequest request,
            CancellationToken cancellationToken)
        {
            var call = new ProbeCall(request, cancellationToken);

            lock (_sync)
            {
                _calls.Add(call);
            }

            return call.Task;
        }

        public async Task<ProbeCall> WaitForCallAsync(long sequenceNumber)
        {
            return await WaitUntilAsync(
                () =>
                {
                    lock (_sync)
                    {
                        return _calls.FirstOrDefault(call => call.Request.SequenceNumber == sequenceNumber);
                    }
                });
        }
    }

    private sealed class ProbeCall
    {
        private readonly TaskCompletionSource<IcmpProbeResponse> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public ProbeCall(IcmpProbeRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            _registration = cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            Task = _completion.Task.ContinueWith(
                task =>
                {
                    _registration.Dispose();
                    return task.GetAwaiter().GetResult();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public IcmpProbeRequest Request { get; }

        public Task<IcmpProbeResponse> Task { get; }

        public void CompleteSuccess(int roundTripTimeMilliseconds)
        {
            _completion.TrySetResult(new IcmpProbeResponse(
                IcmpProbeOutcome.Success,
                TimeSpan.FromMilliseconds(roundTripTimeMilliseconds),
                "fake success"));
        }

        public void CompleteTimeout()
        {
            _completion.TrySetResult(new IcmpProbeResponse(
                IcmpProbeOutcome.Loss,
                RoundTripTime: null,
                "fake timeout"));
        }
    }

    private sealed class MonitorEventSink
    {
        private readonly ConcurrentQueue<IcmpMonitorEvent> _events = new();

        public IReadOnlyList<IcmpMonitorEvent> Events => _events.ToArray();

        public ValueTask AddAsync(IcmpMonitorEvent monitorEvent)
        {
            _events.Enqueue(monitorEvent);
            return ValueTask.CompletedTask;
        }

        public async Task<IcmpMonitorEvent> WaitForEventAsync(IcmpMonitorEventKind kind)
        {
            return await WaitUntilAsync(
                () => _events.FirstOrDefault(monitorEvent => monitorEvent.Kind == kind));
        }

        public async Task WaitForEventCountAsync(IcmpMonitorEventKind kind, int expectedCount)
        {
            await WaitUntilAsync(
                () => _events.Count(monitorEvent => monitorEvent.Kind == kind) >= expectedCount);
        }
    }

    private sealed class MonitorRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Task _task;

        public MonitorRun(CancellationTokenSource cancellation, Task task)
        {
            _cancellation = cancellation;
            _task = task;
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();

            try
            {
                await _task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }

    private static async Task<T> WaitUntilAsync<T>(Func<T?> probe)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The expected monitor test condition was not reached.");
    }

    private static async Task WaitUntilAsync(Func<bool> isComplete)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (isComplete())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The expected monitor test condition was not reached.");
    }
}
