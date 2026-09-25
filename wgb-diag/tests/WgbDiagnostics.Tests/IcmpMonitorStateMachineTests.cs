using WgbDiagnostics.Core.Monitoring;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class IcmpMonitorStateMachineTests
{
    private static readonly DateTimeOffset BaseTimestamp = new(2026, 7, 18, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void OlderTimeoutAfterNewerSuccessDoesNotCreateFalseOutage()
    {
        var stateMachine = new IcmpMonitorStateMachine(
            lossThresholdMilliseconds: 600,
            intervalMilliseconds: 100);
        var events = new List<IcmpMonitorEvent>();

        events.AddRange(stateMachine.Apply(Success(sequenceNumber: 60, startedAt: 6_000, completedAt: 6_007)));
        events.AddRange(stateMachine.Apply(Loss(sequenceNumber: 52, startedAt: 5_200, completedAt: 6_054)));
        events.AddRange(stateMachine.Apply(Success(sequenceNumber: 61, startedAt: 6_100, completedAt: 6_106)));

        Assert.DoesNotContain(events, IsStateTransition);
        Assert.DoesNotContain(events, monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.AlertThresholdReached);
        Assert.DoesNotContain(events, monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.Recovered);

        var lateTimeout = Assert.Single(events.Where(monitorEvent => !monitorEvent.AppliedToState));
        Assert.Equal(IcmpMonitorEventKind.PacketLoss, lateTimeout.Kind);
        Assert.Equal(52, lateTimeout.SequenceNumber);
        Assert.Equal("OutOfOrderTimeoutAfterNewerSuccess", lateTimeout.IgnoredReason);
        Assert.Equal(60, lateTimeout.HighestSequenceAppliedToState);
        Assert.False(lateTimeout.AppliedToState);
        Assert.Equal(0, lateTimeout.EstimatedLossWindowMilliseconds);
        Assert.Equal("late_timeout state_unchanged", lateTimeout.Message);
        Assert.Equal("OK", events[^1].ConnectionState);
    }

    [Fact]
    public void MultipleOlderTimeoutsAfterNewerSuccessesDoNotRegressState()
    {
        var stateMachine = new IcmpMonitorStateMachine(
            lossThresholdMilliseconds: 600,
            intervalMilliseconds: 100);
        var events = new List<IcmpMonitorEvent>();

        events.AddRange(stateMachine.Apply(Success(sequenceNumber: 100, startedAt: 10_000, completedAt: 10_008)));
        for (var sequence = 94; sequence <= 99; sequence++)
        {
            events.AddRange(stateMachine.Apply(Loss(sequence, startedAt: sequence * 100, completedAt: 10_100 + sequence)));
        }

        var ignored = events.Where(monitorEvent => !monitorEvent.AppliedToState).ToArray();

        Assert.Equal(6, ignored.Length);
        Assert.All(ignored, monitorEvent => Assert.Equal(IcmpMonitorEventKind.PacketLoss, monitorEvent.Kind));
        Assert.All(ignored, monitorEvent => Assert.Equal("OutOfOrderTimeoutAfterNewerSuccess", monitorEvent.IgnoredReason));
        Assert.DoesNotContain(events, IsStateTransition);
        Assert.DoesNotContain(events, monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.AlertThresholdReached);
    }

    [Fact]
    public void SustainedOverlappingTimeoutPatternDoesNotCreateReportedFalseOutage()
    {
        var stateMachine = new IcmpMonitorStateMachine(
            lossThresholdMilliseconds: 600,
            intervalMilliseconds: 100);
        var events = new List<IcmpMonitorEvent>();

        for (var cycle = 0; cycle < 50; cycle++)
        {
            var firstSequence = 100 + cycle * 10;
            events.AddRange(stateMachine.Apply(Success(
                sequenceNumber: firstSequence + 8,
                startedAt: firstSequence * 100 + 800,
                completedAt: firstSequence * 100 + 807)));

            for (var sequence = firstSequence; sequence < firstSequence + 8; sequence++)
            {
                events.AddRange(stateMachine.Apply(Loss(
                    sequence,
                    startedAt: sequence * 100,
                    completedAt: firstSequence * 100 + 900 + sequence - firstSequence)));
            }

            events.AddRange(stateMachine.Apply(Success(
                sequenceNumber: firstSequence + 9,
                startedAt: firstSequence * 100 + 900,
                completedAt: firstSequence * 100 + 906)));
        }

        Assert.Equal(400, events.Count(monitorEvent => !monitorEvent.AppliedToState));
        Assert.All(
            events.Where(monitorEvent => !monitorEvent.AppliedToState),
            monitorEvent =>
            {
                Assert.Equal(IcmpMonitorEventKind.PacketLoss, monitorEvent.Kind);
                Assert.Equal("OutOfOrderTimeoutAfterNewerSuccess", monitorEvent.IgnoredReason);
            });
        Assert.DoesNotContain(events, IsStateTransition);
        Assert.DoesNotContain(events, monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.AlertThresholdReached);
        Assert.All(events, monitorEvent => Assert.NotEqual("Loss", monitorEvent.ConnectionState));
    }

    [Fact]
    public void GenuineSequentialOutageCreatesSingleLossAlertAndRecovery()
    {
        var stateMachine = new IcmpMonitorStateMachine(
            lossThresholdMilliseconds: 600,
            intervalMilliseconds: 100);
        var events = new List<IcmpMonitorEvent>();

        events.AddRange(stateMachine.Apply(Success(sequenceNumber: 1, startedAt: 0, completedAt: 10)));
        for (var sequence = 2; sequence <= 7; sequence++)
        {
            events.AddRange(stateMachine.Apply(Loss(sequence, startedAt: sequence * 100, completedAt: sequence * 100 + 1_000)));
        }

        events.AddRange(stateMachine.Apply(Success(sequenceNumber: 8, startedAt: 800, completedAt: 820)));

        var lossStarted = Assert.Single(events.Where(monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.LossStarted));
        var alert = Assert.Single(events.Where(monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.AlertThresholdReached));
        var recovered = Assert.Single(events.Where(monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.Recovered));

        Assert.Equal(2, lossStarted.SequenceNumber);
        Assert.Equal(1, lossStarted.ConsecutiveLoss);
        Assert.Equal(100, lossStarted.EstimatedLossWindowMilliseconds);
        Assert.Equal(7, alert.SequenceNumber);
        Assert.Equal(6, alert.ConsecutiveLoss);
        Assert.Equal(600, alert.EstimatedLossWindowMilliseconds);
        Assert.Equal(8, recovered.SequenceNumber);
        Assert.Equal(6, recovered.ConsecutiveLoss);
        Assert.Equal(600, recovered.EstimatedLossWindowMilliseconds);
        Assert.All(events, monitorEvent => Assert.True(monitorEvent.AppliedToState));
    }

    [Fact]
    public void NewerTimeoutBeforeOlderSuccessIsDeterministic()
    {
        var stateMachine = new IcmpMonitorStateMachine(
            lossThresholdMilliseconds: 600,
            intervalMilliseconds: 100);

        var lossEvents = stateMachine.Apply(Loss(sequenceNumber: 2, startedAt: 200, completedAt: 1_200));
        var oldSuccessEvents = stateMachine.Apply(Success(sequenceNumber: 1, startedAt: 100, completedAt: 1_250));
        var recoveryEvents = stateMachine.Apply(Success(sequenceNumber: 3, startedAt: 300, completedAt: 1_260));

        Assert.Equal(IcmpMonitorEventKind.LossStarted, Assert.Single(lossEvents).Kind);

        var oldSuccess = Assert.Single(oldSuccessEvents);
        Assert.Equal(IcmpMonitorEventKind.PingReply, oldSuccess.Kind);
        Assert.False(oldSuccess.AppliedToState);
        Assert.Equal("OutOfOrderSuccess", oldSuccess.IgnoredReason);
        Assert.DoesNotContain(oldSuccessEvents, monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.Recovered);

        Assert.Equal(
            [IcmpMonitorEventKind.PingReply, IcmpMonitorEventKind.Recovered],
            recoveryEvents.Select(monitorEvent => monitorEvent.Kind).ToArray());
    }

    [Fact]
    public void DelayedOlderSuccessDoesNotRecoverNewerGenuineOutage()
    {
        var stateMachine = new IcmpMonitorStateMachine(
            lossThresholdMilliseconds: 600,
            intervalMilliseconds: 100);

        stateMachine.Apply(Loss(sequenceNumber: 10, startedAt: 1_000, completedAt: 2_000));
        stateMachine.Apply(Loss(sequenceNumber: 11, startedAt: 1_100, completedAt: 2_100));
        var delayedSuccessEvents = stateMachine.Apply(Success(sequenceNumber: 9, startedAt: 900, completedAt: 2_150));

        var delayedSuccess = Assert.Single(delayedSuccessEvents);
        Assert.Equal(IcmpMonitorEventKind.PingReply, delayedSuccess.Kind);
        Assert.False(delayedSuccess.AppliedToState);
        Assert.Equal("OutOfOrderSuccess", delayedSuccess.IgnoredReason);
        Assert.DoesNotContain(delayedSuccessEvents, monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.Recovered);
        Assert.Equal("Loss", delayedSuccess.ConnectionState);

        var recoveryEvents = stateMachine.Apply(Success(sequenceNumber: 12, startedAt: 1_200, completedAt: 2_200));

        Assert.Contains(recoveryEvents, monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.Recovered);
    }

    [Fact]
    public void AlertThresholdUsesScriptStyleConsecutiveLossWindow()
    {
        var thresholdOne = new IcmpMonitorStateMachine(
            lossThresholdMilliseconds: 1,
            intervalMilliseconds: 100);
        var firstLossEvents = thresholdOne.Apply(Loss(sequenceNumber: 1, startedAt: 100, completedAt: 1_100));

        Assert.Equal(
            [IcmpMonitorEventKind.LossStarted, IcmpMonitorEventKind.AlertThresholdReached],
            firstLossEvents.Select(monitorEvent => monitorEvent.Kind).ToArray());
        Assert.Equal(100, firstLossEvents[^1].EstimatedLossWindowMilliseconds);

        var thresholdGreaterThanTwoPackets = new IcmpMonitorStateMachine(
            lossThresholdMilliseconds: 250,
            intervalMilliseconds: 100);

        var loss1 = thresholdGreaterThanTwoPackets.Apply(Loss(sequenceNumber: 1, startedAt: 100, completedAt: 1_100));
        var loss2 = thresholdGreaterThanTwoPackets.Apply(Loss(sequenceNumber: 2, startedAt: 200, completedAt: 1_200));
        var loss3 = thresholdGreaterThanTwoPackets.Apply(Loss(sequenceNumber: 3, startedAt: 300, completedAt: 1_300));

        Assert.DoesNotContain(loss1, monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.AlertThresholdReached);
        Assert.DoesNotContain(loss2, monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.AlertThresholdReached);
        var alert = Assert.Single(loss3.Where(monitorEvent => monitorEvent.Kind == IcmpMonitorEventKind.AlertThresholdReached));
        Assert.Equal(300, alert.EstimatedLossWindowMilliseconds);
    }

    private static bool IsStateTransition(IcmpMonitorEvent monitorEvent)
    {
        return monitorEvent.Kind is IcmpMonitorEventKind.LossStarted
            or IcmpMonitorEventKind.Recovered;
    }

    private static IcmpProbeResult Success(long sequenceNumber, long startedAt, long completedAt)
    {
        return new IcmpProbeResult(
            IcmpProbeOutcome.Success,
            BaseTimestamp.AddMilliseconds(completedAt),
            sequenceNumber,
            startedAt,
            completedAt,
            TimeSpan.FromMilliseconds(completedAt - startedAt),
            "ok");
    }

    private static IcmpProbeResult Loss(long sequenceNumber, long startedAt, long completedAt)
    {
        return new IcmpProbeResult(
            IcmpProbeOutcome.Loss,
            BaseTimestamp.AddMilliseconds(completedAt),
            sequenceNumber,
            startedAt,
            completedAt,
            RoundTripTime: null,
            "loss");
    }
}
