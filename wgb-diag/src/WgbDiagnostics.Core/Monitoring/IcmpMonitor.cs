using System.Collections.Concurrent;
using System.Diagnostics;

namespace WgbDiagnostics.Core.Monitoring;

public sealed class IcmpMonitor : IIcmpMonitor
{
    private readonly IIcmpProbe _probe;
    private readonly object _stateLock = new();

    public IcmpMonitor(IIcmpProbe probe)
    {
        _probe = probe;
    }

    public async Task RunAsync(
        IcmpMonitorOptions options,
        Func<IcmpMonitorEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        var stateMachine = new IcmpMonitorStateMachine(options.LossThresholdMilliseconds);
        var pendingProbes = new ConcurrentDictionary<long, Task>();
        var originTimestamp = Stopwatch.GetTimestamp();
        var sequenceNumber = 0L;

        try
        {
            ScheduleProbe();

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.IntervalMilliseconds));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                ScheduleProbe();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await Task.WhenAll(pendingProbes.Values);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        void ScheduleProbe()
        {
            var sequence = Interlocked.Increment(ref sequenceNumber);
            var startedAtTimestamp = Stopwatch.GetTimestamp();
            var startedAtMilliseconds = GetElapsedMilliseconds(originTimestamp, startedAtTimestamp);
            var task = ProbeAndPublishAsync(
                options,
                stateMachine,
                onEvent,
                sequence,
                originTimestamp,
                startedAtMilliseconds,
                cancellationToken);

            pendingProbes[sequence] = task;
            _ = task.ContinueWith(
                _ => pendingProbes.TryRemove(sequence, out var _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ProbeAndPublishAsync(
        IcmpMonitorOptions options,
        IcmpMonitorStateMachine stateMachine,
        Func<IcmpMonitorEvent, ValueTask> onEvent,
        long sequenceNumber,
        long originTimestamp,
        long startedAtMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendProbeAsync(
                options,
                sequenceNumber,
                originTimestamp,
                startedAtMilliseconds,
                cancellationToken);

            IReadOnlyList<IcmpMonitorEvent> events;
            lock (_stateLock)
            {
                events = stateMachine.Apply(result);
            }

            foreach (var monitorEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await onEvent(monitorEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<IcmpProbeResult> SendProbeAsync(
        IcmpMonitorOptions options,
        long sequenceNumber,
        long originTimestamp,
        long startedAtMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new IcmpProbeRequest(
                options.Target,
                options.TimeoutMilliseconds,
                sequenceNumber,
                startedAtMilliseconds);
            var response = await _probe.SendAsync(request, cancellationToken);
            var completedAtTimestamp = Stopwatch.GetTimestamp();

            return new IcmpProbeResult(
                response.Outcome,
                DateTimeOffset.UtcNow,
                sequenceNumber,
                startedAtMilliseconds,
                GetElapsedMilliseconds(originTimestamp, completedAtTimestamp),
                response.RoundTripTime,
                response.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var completedAtTimestamp = Stopwatch.GetTimestamp();
            return new IcmpProbeResult(
                IcmpProbeOutcome.Error,
                DateTimeOffset.UtcNow,
                sequenceNumber,
                startedAtMilliseconds,
                GetElapsedMilliseconds(originTimestamp, completedAtTimestamp),
                RoundTripTime: null,
                $"ICMP probe failed: {ex.Message}");
        }
    }

    private static long GetElapsedMilliseconds(long originTimestamp, long timestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(originTimestamp, timestamp);
        return Math.Max(0, (long)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero));
    }
}
