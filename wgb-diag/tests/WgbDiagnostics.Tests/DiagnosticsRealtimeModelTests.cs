using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Realtime;
using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class DiagnosticsRealtimeModelTests
{
    private static readonly DateTimeOffset BaseTimestamp = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SuccessEventsCreateRttPoints()
    {
        var model = new DiagnosticsRealtimeModel();

        model.Apply(Ping(IcmpMonitorEventKind.PingReply, seconds: 0, rttMilliseconds: 7));
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, seconds: 1, rttMilliseconds: 9));

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(1));
        var segment = Assert.Single(snapshot.RttSegments);
        Assert.Equal(new[] { 7d, 9d }, segment.Points.Select(point => point.RoundTripTimeMilliseconds).ToArray());
        Assert.Equal(2, snapshot.PingStatus.TotalOk);
        Assert.Equal(TimeSpan.FromMilliseconds(9), snapshot.PingStatus.CurrentRoundTripTime);
    }

    [Fact]
    public void LossEventsCreateGapsInsteadOfZeroPoints()
    {
        var model = new DiagnosticsRealtimeModel();

        model.Apply(Ping(IcmpMonitorEventKind.PingReply, seconds: 0, rttMilliseconds: 5));
        model.Apply(Ping(IcmpMonitorEventKind.LossStarted, seconds: 1, consecutiveLoss: 1, lossWindow: 100));
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, seconds: 2, rttMilliseconds: 8));

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(2));

        Assert.Equal(2, snapshot.RttSegments.Count);
        Assert.All(snapshot.RttSegments, segment => Assert.Single(segment.Points));
        Assert.DoesNotContain(
            snapshot.RttSegments.SelectMany(segment => segment.Points),
            point => point.RoundTripTimeMilliseconds == 0);
        Assert.Contains(snapshot.Markers, marker => marker.Kind == RealtimeGraphMarkerKind.LossStarted);
    }

    [Fact]
    public void WindowTrimmingRemovesOldGuiPointsButKeepsCurrentStatus()
    {
        var model = new DiagnosticsRealtimeModel(new RealtimeGraphOptions(
            TimeSpan.FromMinutes(1),
            MaxDataPoints: 100,
            MaxMarkers: 100));

        model.Apply(Ping(IcmpMonitorEventKind.PingReply, seconds: 0, rttMilliseconds: 5));
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, seconds: 30, rttMilliseconds: 6));
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, seconds: 90, rttMilliseconds: 7));

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(90));
        var points = snapshot.RttSegments.SelectMany(segment => segment.Points).ToArray();

        Assert.Equal(new[] { 6d, 7d }, points.Select(point => point.RoundTripTimeMilliseconds).ToArray());
        Assert.Equal(3, snapshot.PingStatus.TotalOk);
    }

    [Fact]
    public void ParentApChangedCreatesRoamMarkerWithDetails()
    {
        var model = new DiagnosticsRealtimeModel();

        model.Apply(new WgbPollEvent(
            WgbPollEventKind.ParentApChanged,
            BaseTimestamp,
            Association("ap-b", "22:22:22:22:22:22", "36", "1"),
            ParseResult: null,
            RawOutput: null,
            Message: "roam",
            OldParentApName: "ap-a",
            NewParentApName: "ap-b",
            OldParentBssid: "11:11:11:11:11:11",
            NewParentBssid: "22:22:22:22:22:22",
            OldChannel: "11",
            NewChannel: "36",
            OldRadioId: "0",
            NewRadioId: "1",
            RoamClassification: WgbRoamClassification.DifferentApDifferentChannel));

        var snapshot = model.Snapshot(BaseTimestamp);
        var marker = Assert.Single(snapshot.Markers);

        Assert.Equal(RealtimeGraphMarkerKind.ParentApChanged, marker.Kind);
        Assert.Equal("ap-a", marker.OldParentApName);
        Assert.Equal("ap-b", marker.NewParentApName);
        Assert.Equal("11", marker.OldChannel);
        Assert.Equal("36", marker.NewChannel);
        Assert.Equal(WgbRoamClassification.DifferentApDifferentChannel, marker.RoamClassification);
        Assert.Equal("ap-b", snapshot.WgbStatus.ParentApName);
    }

    [Fact]
    public void OutOfOrderLossEventSplitsExistingSuccessSeries()
    {
        var model = new DiagnosticsRealtimeModel();

        model.Apply(Ping(IcmpMonitorEventKind.PingReply, seconds: 0, rttMilliseconds: 5));
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, seconds: 20, rttMilliseconds: 9));
        model.Apply(Ping(IcmpMonitorEventKind.LossStarted, seconds: 10, consecutiveLoss: 1, lossWindow: 100));

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(20));

        Assert.Equal(2, snapshot.RttSegments.Count);
        Assert.Equal(5, snapshot.RttSegments[0].Points.Single().RoundTripTimeMilliseconds);
        Assert.Equal(9, snapshot.RttSegments[1].Points.Single().RoundTripTimeMilliseconds);
        Assert.Equal(BaseTimestamp.AddSeconds(10), Assert.Single(snapshot.Markers).Timestamp);
    }

    private static IcmpMonitorEvent Ping(
        IcmpMonitorEventKind kind,
        int seconds,
        int rttMilliseconds = 0,
        int consecutiveLoss = 0,
        int lossWindow = 0)
    {
        return new IcmpMonitorEvent(
            kind,
            BaseTimestamp.AddSeconds(seconds),
            seconds + 1,
            rttMilliseconds > 0 ? TimeSpan.FromMilliseconds(rttMilliseconds) : null,
            consecutiveLoss,
            lossWindow,
            kind.ToString());
    }

    private static WgbAssociationSnapshot Association(
        string parentAp,
        string bssid,
        string channel,
        string radioId)
    {
        return new WgbAssociationSnapshot(
            parentAp,
            bssid,
            channel,
            Rssi: "-61",
            radioId,
            TxRate: "144.4",
            RxRate: "130.0",
            WgbIp: "192.168.1.20",
            AssociationStatus: "Associated",
            CandidateApName: null,
            CandidateBssid: null);
    }
}
