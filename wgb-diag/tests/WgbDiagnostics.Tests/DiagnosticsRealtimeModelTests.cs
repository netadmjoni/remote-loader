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
        Assert.Equal("0", marker.OldRadioId);
        Assert.Equal("1", marker.NewRadioId);
        Assert.Equal(WgbRoamClassification.DifferentApDifferentChannel, marker.RoamClassification);
        Assert.Equal("ap-b", snapshot.WgbStatus.ParentApName);

        var roamEvent = Assert.Single(snapshot.RoamEvents);
        Assert.Equal(marker.Timestamp, roamEvent.Timestamp);
        Assert.Equal("ap-a", roamEvent.OldParentApName);
        Assert.Equal("ap-b", roamEvent.NewParentApName);
        Assert.Equal("11", roamEvent.OldChannel);
        Assert.Equal("36", roamEvent.NewChannel);
        Assert.Equal("0", roamEvent.OldRadioId);
        Assert.Equal("1", roamEvent.NewRadioId);
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

    [Fact]
    public void WgbPollsWithRssiCreateRssiPoints()
    {
        var model = new DiagnosticsRealtimeModel();

        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, seconds: 0, Association("ap-a", "11:11:11:11:11:11", "11", "0", "-61 dBm")));
        model.Apply(Wgb(WgbPollEventKind.AssociationUpdated, seconds: 1, Association("ap-a", "11:11:11:11:11:11", "11", "0", "-58")));

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(1));

        Assert.Equal(new[] { -61d, -58d }, snapshot.RssiPoints.Select(point => point.Rssi).ToArray());
        Assert.Equal("ap-a", snapshot.WgbStatus.ParentApName);
        Assert.Equal("-58", snapshot.WgbStatus.Rssi);
    }

    [Fact]
    public void RssiWindowTrimmingUsesGraphVisibleMinutes()
    {
        var model = new DiagnosticsRealtimeModel(new RealtimeGraphOptions(
            TimeSpan.FromMinutes(1),
            MaxDataPoints: 100,
            MaxMarkers: 100));

        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, seconds: 0, Association("ap-a", "11:11:11:11:11:11", "11", "0", "-70")));
        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, seconds: 30, Association("ap-a", "11:11:11:11:11:11", "11", "0", "-65")));
        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, seconds: 90, Association("ap-a", "11:11:11:11:11:11", "11", "0", "-60")));

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(90));

        Assert.Equal(new[] { -65d, -60d }, snapshot.RssiPoints.Select(point => point.Rssi).ToArray());
    }

    [Fact]
    public void MissingRssiValuesDoNotCreateRssiPoints()
    {
        var model = new DiagnosticsRealtimeModel();

        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, seconds: 0, Association("ap-a", "11:11:11:11:11:11", "11", "0", rssi: null)));
        model.Apply(Wgb(WgbPollEventKind.AssociationUpdated, seconds: 1, Association("ap-a", "11:11:11:11:11:11", "11", "0", "unknown")));

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(1));

        Assert.Empty(snapshot.RssiPoints);
        Assert.Equal("unknown", snapshot.WgbStatus.Rssi);
    }

    [Fact]
    public void RoamEventAppearsAsGraphMarkerAtSameTimestamp()
    {
        var model = new DiagnosticsRealtimeModel();

        model.Apply(new WgbPollEvent(
            WgbPollEventKind.ParentApChanged,
            BaseTimestamp.AddSeconds(5),
            Association("ap-b", "22:22:22:22:22:22", "11", "1", "-62"),
            ParseResult: null,
            RawOutput: null,
            Message: "same-channel roam",
            OldParentApName: "ap-a",
            NewParentApName: "ap-b",
            OldParentBssid: "11:11:11:11:11:11",
            NewParentBssid: "22:22:22:22:22:22",
            OldChannel: "11",
            NewChannel: "11",
            OldRadioId: "0",
            NewRadioId: "1",
            RoamClassification: WgbRoamClassification.DifferentApSameChannel));

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(5));
        var marker = Assert.Single(snapshot.Markers.Where(marker => marker.Kind == RealtimeGraphMarkerKind.ParentApChanged));
        var roamEvent = Assert.Single(snapshot.RoamEvents);

        Assert.Equal(roamEvent.Timestamp, marker.Timestamp);
        Assert.Equal(roamEvent.OldParentApName, marker.OldParentApName);
        Assert.Equal(roamEvent.NewParentApName, marker.NewParentApName);
        Assert.Equal(roamEvent.OldChannel, marker.OldChannel);
        Assert.Equal(roamEvent.NewChannel, marker.NewChannel);
        Assert.Equal(roamEvent.OldRadioId, marker.OldRadioId);
        Assert.Equal(roamEvent.NewRadioId, marker.NewRadioId);
        Assert.Equal(roamEvent.RoamClassification, marker.RoamClassification);
    }

    [Fact]
    public void ClearGraphRemovesRealtimeGraphArtifactsButKeepsCurrentStatus()
    {
        var model = new DiagnosticsRealtimeModel();
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, seconds: 0, rttMilliseconds: 5));
        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, seconds: 1, Association("ap-a", "11:11:11:11:11:11", "11", "0", "-61")));
        model.Apply(new WgbPollEvent(
            WgbPollEventKind.ParentApChanged,
            BaseTimestamp.AddSeconds(2),
            Association("ap-b", "22:22:22:22:22:22", "36", "1", "-62"),
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

        model.ClearGraph();

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(2));
        Assert.Empty(snapshot.RttSegments);
        Assert.Empty(snapshot.RssiPoints);
        Assert.Empty(snapshot.Markers);
        Assert.Empty(snapshot.RoamEvents);
        Assert.Equal("ap-b", snapshot.WgbStatus.ParentApName);
        Assert.Equal(1, snapshot.PingStatus.TotalOk);
    }

    [Fact]
    public void DuplicateMarkerEventsAreStoredOnce()
    {
        var model = new DiagnosticsRealtimeModel();
        var lossStarted = Ping(IcmpMonitorEventKind.LossStarted, seconds: 1, consecutiveLoss: 1, lossWindow: 100);
        var roam = ParentChanged(seconds: 2);

        model.Apply(lossStarted);
        model.Apply(lossStarted);
        model.Apply(roam);
        model.Apply(roam);

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(2));

        Assert.Equal(2, snapshot.Markers.Count);
        Assert.Single(snapshot.Markers.Where(marker => marker.Kind == RealtimeGraphMarkerKind.LossStarted));
        Assert.Single(snapshot.Markers.Where(marker => marker.Kind == RealtimeGraphMarkerKind.ParentApChanged));
        Assert.Single(snapshot.RoamEvents);
    }

    [Fact]
    public void RepeatedSnapshotsDoNotDuplicateMarkers()
    {
        var model = new DiagnosticsRealtimeModel();
        model.Apply(Ping(IcmpMonitorEventKind.LossStarted, seconds: 1, consecutiveLoss: 1, lossWindow: 100));

        var first = model.Snapshot(BaseTimestamp.AddSeconds(1));
        var second = model.Snapshot(BaseTimestamp.AddSeconds(1));

        Assert.Single(first.Markers);
        Assert.Single(second.Markers);
    }

    [Fact]
    public void MarkerWindowTrimmingRemovesOldMarkers()
    {
        var model = new DiagnosticsRealtimeModel(new RealtimeGraphOptions(
            TimeSpan.FromMinutes(1),
            MaxDataPoints: 100,
            MaxMarkers: 100));

        model.Apply(Ping(IcmpMonitorEventKind.LossStarted, seconds: 0, consecutiveLoss: 1, lossWindow: 100));
        model.Apply(Ping(IcmpMonitorEventKind.LossStarted, seconds: 90, consecutiveLoss: 1, lossWindow: 100));

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(90));
        var marker = Assert.Single(snapshot.Markers);

        Assert.Equal(BaseTimestamp.AddSeconds(90), marker.Timestamp);
    }

    [Fact]
    public void MaxDataPointsAndMarkersLimitGraphState()
    {
        var model = new DiagnosticsRealtimeModel(new RealtimeGraphOptions(
            TimeSpan.FromHours(1),
            MaxDataPoints: 3,
            MaxMarkers: 2));

        for (var second = 0; second < 5; second++)
        {
            model.Apply(Wgb(WgbPollEventKind.PollSucceeded, second, Association("ap-a", "11:11:11:11:11:11", "11", "0", $"-6{second}")));
        }

        for (var second = 10; second < 15; second++)
        {
            model.Apply(Ping(IcmpMonitorEventKind.LossStarted, seconds: second, consecutiveLoss: 1, lossWindow: 100));
        }

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(14));

        Assert.Equal(3, snapshot.RssiPoints.Count);
        Assert.Equal(2, snapshot.Markers.Count);
    }

    [Fact]
    public void CurrentLossWindowTracksActiveLossAndRecovery()
    {
        var model = new DiagnosticsRealtimeModel();

        model.Apply(Ping(IcmpMonitorEventKind.LossStarted, seconds: 1, consecutiveLoss: 1, lossWindow: 100));
        model.Apply(Ping(IcmpMonitorEventKind.AlertThresholdReached, seconds: 2, consecutiveLoss: 2, lossWindow: 650));

        var outageSnapshot = model.Snapshot(BaseTimestamp.AddSeconds(2));
        Assert.Equal(TimeSpan.FromMilliseconds(650), outageSnapshot.PingStatus.CurrentLossWindow);
        Assert.Equal(BaseTimestamp.AddSeconds(1), outageSnapshot.PingStatus.CurrentLossStartedAt);

        model.Apply(Ping(IcmpMonitorEventKind.Recovered, seconds: 3, rttMilliseconds: 8));

        var recoveredSnapshot = model.Snapshot(BaseTimestamp.AddSeconds(3));
        Assert.Equal(TimeSpan.Zero, recoveredSnapshot.PingStatus.CurrentLossWindow);
        Assert.Null(recoveredSnapshot.PingStatus.CurrentLossStartedAt);
    }

    [Fact]
    public void RoamTimelineKeepsRssiBeforeRoamWhenAvailable()
    {
        var model = new DiagnosticsRealtimeModel();

        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, seconds: 1, Association("ap-a", "11:11:11:11:11:11", "11", "0", "-67")));
        model.Apply(ParentChanged(seconds: 2));

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(2));
        var roamEvent = Assert.Single(snapshot.RoamEvents);

        Assert.Equal("-67", roamEvent.OldRssi);
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

    private static WgbPollEvent Wgb(
        WgbPollEventKind kind,
        int seconds,
        WgbAssociationSnapshot association)
    {
        return new WgbPollEvent(
            kind,
            BaseTimestamp.AddSeconds(seconds),
            association,
            ParseResult: null,
            RawOutput: null,
            Message: null);
    }

    private static WgbPollEvent ParentChanged(int seconds)
    {
        return new WgbPollEvent(
            WgbPollEventKind.ParentApChanged,
            BaseTimestamp.AddSeconds(seconds),
            Association("ap-b", "22:22:22:22:22:22", "36", "1", "-62"),
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
            RoamClassification: WgbRoamClassification.DifferentApDifferentChannel);
    }

    private static WgbAssociationSnapshot Association(
        string parentAp,
        string bssid,
        string channel,
        string radioId,
        string? rssi = "-61")
    {
        return new WgbAssociationSnapshot(
            parentAp,
            bssid,
            channel,
            rssi,
            radioId,
            TxRate: "144.4",
            RxRate: "130.0",
            WgbIp: "192.168.1.20",
            AssociationStatus: "Associated",
            CandidateApName: null,
            CandidateBssid: null);
    }
}
