using WgbDiagnostics.Core.Monitoring;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class PingEventViewModelTests
{
    private static readonly DateTimeOffset BaseTimestamp = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EventViewSuppressesOrdinaryPingReplies()
    {
        var model = new PingEventViewModel();

        var rows = model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, rttMilliseconds: 7), PingEventViewMode.Event);

        Assert.Empty(rows);
    }

    [Fact]
    public void EventViewShowsLastOkBeforeLossStart()
    {
        var model = new PingEventViewModel();
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, rttMilliseconds: 7), PingEventViewMode.Event);

        var rows = model.Apply(Ping(IcmpMonitorEventKind.LossStarted, sequence: 2, consecutiveLoss: 1, lossWindow: 100), PingEventViewMode.Event);

        Assert.Equal(new[] { "LAST_OK", "LOSS_START" }, rows.Select(row => row.EventName).ToArray());
        Assert.Equal(PingEventSeverity.Success, rows[0].Severity);
        Assert.Equal(PingEventSeverity.Warning, rows[1].Severity);
    }

    [Fact]
    public void EventViewClassifiesAlertRecoveryAndError()
    {
        var model = new PingEventViewModel();

        var alert = Assert.Single(model.Apply(Ping(IcmpMonitorEventKind.AlertThresholdReached, sequence: 3, consecutiveLoss: 3, lossWindow: 600), PingEventViewMode.Event));
        var recovery = Assert.Single(model.Apply(Ping(IcmpMonitorEventKind.Recovered, sequence: 4, rttMilliseconds: 9), PingEventViewMode.Event));
        var error = Assert.Single(model.Apply(Ping(IcmpMonitorEventKind.Error, sequence: 5, consecutiveLoss: 1, lossWindow: 100, message: "probe failed"), PingEventViewMode.Event));

        Assert.Equal(PingEventSeverity.Critical, alert.Severity);
        Assert.Equal("ALERT", alert.EventName);
        Assert.Equal(PingEventSeverity.Success, recovery.Severity);
        Assert.Equal("RECOVER", recovery.EventName);
        Assert.Equal(PingEventSeverity.Critical, error.Severity);
        Assert.Equal("ERROR", error.EventName);
    }

    [Fact]
    public void RawViewShowsEveryProbe()
    {
        var model = new PingEventViewModel();

        var rows = model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, rttMilliseconds: 7), PingEventViewMode.Raw);

        var row = Assert.Single(rows);
        Assert.Equal("PingReply", row.EventName);
        Assert.Equal(PingEventSeverity.Neutral, row.Severity);
        Assert.Contains("rtt=7 ms", row.Text);
    }

    [Fact]
    public void LateTimeoutIsVisibleAsPacketLossInEventViewAndDebuggedInRawView()
    {
        var model = new PingEventViewModel();
        var lateTimeout = Ping(
            IcmpMonitorEventKind.PacketLoss,
            sequence: 52,
            message: "late_timeout state_unchanged",
            appliedToState: false,
            ignoredReason: "OutOfOrderTimeoutAfterNewerSuccess");

        var eventRow = Assert.Single(model.Apply(lateTimeout, PingEventViewMode.Event));
        Assert.Equal("PACKET_LOSS", eventRow.EventName);
        Assert.Equal(PingEventSeverity.Warning, eventRow.Severity);
        Assert.Contains("late_timeout state_unchanged", eventRow.Text);
        Assert.DoesNotContain("ignored_for_state", eventRow.Text);

        var row = Assert.Single(model.Apply(lateTimeout, PingEventViewMode.Raw));
        Assert.Equal("LATE_TIMEOUT", row.EventName);
        Assert.Contains("ignored_for_state", row.Text);
        Assert.Contains("OutOfOrderTimeoutAfterNewerSuccess", row.Text);
    }

    [Fact]
    public void ResetForgetsLastOk()
    {
        var model = new PingEventViewModel();
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, rttMilliseconds: 7), PingEventViewMode.Event);

        model.Reset();
        var rows = model.Apply(Ping(IcmpMonitorEventKind.LossStarted, sequence: 2, consecutiveLoss: 1, lossWindow: 100), PingEventViewMode.Event);

        var row = Assert.Single(rows);
        Assert.Equal("LOSS_START", row.EventName);
    }

    private static IcmpMonitorEvent Ping(
        IcmpMonitorEventKind kind,
        long sequence,
        int rttMilliseconds = 0,
        int consecutiveLoss = 0,
        int lossWindow = 0,
        string? message = null,
        bool appliedToState = true,
        string? ignoredReason = null)
    {
        return new IcmpMonitorEvent(
            kind,
            BaseTimestamp.AddMilliseconds(sequence * 100),
            sequence,
            rttMilliseconds > 0 ? TimeSpan.FromMilliseconds(rttMilliseconds) : null,
            consecutiveLoss,
            lossWindow,
            message,
            AppliedToState: appliedToState,
            IgnoredReason: ignoredReason);
    }
}
