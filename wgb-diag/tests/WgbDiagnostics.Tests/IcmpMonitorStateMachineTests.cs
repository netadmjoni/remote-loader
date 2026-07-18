using WgbDiagnostics.Core.Monitoring;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class IcmpMonitorStateMachineTests
{
    private static readonly DateTimeOffset BaseTimestamp = new(2026, 7, 18, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void OkThenLossStartsLossState()
    {
        var stateMachine = new IcmpMonitorStateMachine(lossThresholdMilliseconds: 600);

        var okEvents = stateMachine.Apply(Success(sequenceNumber: 1, startedAt: 0, completedAt: 10));
        var lossEvents = stateMachine.Apply(Loss(sequenceNumber: 2, startedAt: 100, completedAt: 200));

        Assert.Equal(IcmpMonitorEventKind.PingReply, Assert.Single(okEvents).Kind);

        var lossStarted = Assert.Single(lossEvents);
        Assert.Equal(IcmpMonitorEventKind.LossStarted, lossStarted.Kind);
        Assert.Equal(2, lossStarted.SequenceNumber);
        Assert.Equal(1, lossStarted.ConsecutiveLoss);
        Assert.Equal(100, lossStarted.EstimatedLossWindowMilliseconds);
    }

    [Fact]
    public void ContinuedLossIncrementsConsecutiveLoss()
    {
        var stateMachine = new IcmpMonitorStateMachine(lossThresholdMilliseconds: 600);

        stateMachine.Apply(Success(sequenceNumber: 1, startedAt: 0, completedAt: 10));
        stateMachine.Apply(Loss(sequenceNumber: 2, startedAt: 100, completedAt: 200));
        var events = stateMachine.Apply(Loss(sequenceNumber: 3, startedAt: 200, completedAt: 300));

        var loss = Assert.Single(events);
        Assert.Equal(IcmpMonitorEventKind.Loss, loss.Kind);
        Assert.Equal(2, loss.ConsecutiveLoss);
        Assert.Equal(200, loss.EstimatedLossWindowMilliseconds);
    }

    [Fact]
    public void ThresholdAlertIsRaisedOnceWhenLossWindowReachesThreshold()
    {
        var stateMachine = new IcmpMonitorStateMachine(lossThresholdMilliseconds: 600);

        stateMachine.Apply(Success(sequenceNumber: 1, startedAt: 0, completedAt: 10));
        stateMachine.Apply(Loss(sequenceNumber: 2, startedAt: 100, completedAt: 200));
        stateMachine.Apply(Loss(sequenceNumber: 3, startedAt: 200, completedAt: 300));
        var thresholdEvents = stateMachine.Apply(Loss(sequenceNumber: 4, startedAt: 600, completedAt: 700));
        var nextLossEvents = stateMachine.Apply(Loss(sequenceNumber: 5, startedAt: 700, completedAt: 800));

        Assert.Equal(
            [IcmpMonitorEventKind.Loss, IcmpMonitorEventKind.AlertThresholdReached],
            thresholdEvents.Select(monitorEvent => monitorEvent.Kind).ToArray());
        Assert.Equal(600, thresholdEvents.Last().EstimatedLossWindowMilliseconds);

        var nextLoss = Assert.Single(nextLossEvents);
        Assert.Equal(IcmpMonitorEventKind.Loss, nextLoss.Kind);
        Assert.Equal(4, nextLoss.ConsecutiveLoss);
    }

    [Fact]
    public void SuccessfulProbeAfterLossRaisesRecovery()
    {
        var stateMachine = new IcmpMonitorStateMachine(lossThresholdMilliseconds: 600);

        stateMachine.Apply(Success(sequenceNumber: 1, startedAt: 0, completedAt: 10));
        stateMachine.Apply(Loss(sequenceNumber: 2, startedAt: 100, completedAt: 200));
        stateMachine.Apply(Loss(sequenceNumber: 3, startedAt: 200, completedAt: 300));
        var events = stateMachine.Apply(Success(sequenceNumber: 4, startedAt: 800, completedAt: 820));

        Assert.Equal(
            [IcmpMonitorEventKind.PingReply, IcmpMonitorEventKind.Recovered],
            events.Select(monitorEvent => monitorEvent.Kind).ToArray());

        var recovered = events.Last();
        Assert.Equal(2, recovered.ConsecutiveLoss);
        Assert.Equal(720, recovered.EstimatedLossWindowMilliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(20), recovered.RoundTripTime);
    }

    [Fact]
    public void NewLossAfterRecoveryStartsNewLossWindow()
    {
        var stateMachine = new IcmpMonitorStateMachine(lossThresholdMilliseconds: 600);

        stateMachine.Apply(Success(sequenceNumber: 1, startedAt: 0, completedAt: 10));
        stateMachine.Apply(Loss(sequenceNumber: 2, startedAt: 100, completedAt: 200));
        stateMachine.Apply(Success(sequenceNumber: 3, startedAt: 300, completedAt: 320));
        var events = stateMachine.Apply(Loss(sequenceNumber: 4, startedAt: 900, completedAt: 950));

        var lossStarted = Assert.Single(events);
        Assert.Equal(IcmpMonitorEventKind.LossStarted, lossStarted.Kind);
        Assert.Equal(1, lossStarted.ConsecutiveLoss);
        Assert.Equal(50, lossStarted.EstimatedLossWindowMilliseconds);
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
