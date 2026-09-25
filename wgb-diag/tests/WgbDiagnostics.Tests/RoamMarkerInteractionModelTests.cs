using WgbDiagnostics.Core.Realtime;
using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class RoamMarkerInteractionModelTests
{
    private static readonly DateTimeOffset BaseTimestamp = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectNearestChoosesClickedRoamMarkerWithinPixelTolerance()
    {
        var roams = new[]
        {
            Roam(5, "roam-a"),
            Roam(10, "roam-b"),
            Roam(15, "roam-c")
        };
        var targets = RoamMarkerSelectionModel.CreateHitTargets(
            roams,
            new GraphAxisLimits(0, 20),
            plotWidthPixels: 200,
            SecondsFromBase);
        var selection = new RoamMarkerSelectionModel();

        var selected = selection.SelectNearest(targets, clickPixelX: 104);

        Assert.Equal("roam-b", selected);
        Assert.True(selection.IsSelected("roam-b"));
    }

    [Fact]
    public void SelectNearestChoosesNearestMarkerAndClearsWhenClickIsEmpty()
    {
        var targets = RoamMarkerSelectionModel.CreateHitTargets(
            [Roam(10, "roam-a"), Roam(12, "roam-b")],
            new GraphAxisLimits(0, 20),
            plotWidthPixels: 200,
            SecondsFromBase);
        var selection = new RoamMarkerSelectionModel();

        Assert.Equal("roam-b", selection.SelectNearest(targets, clickPixelX: 117));
        Assert.Null(selection.SelectNearest(targets, clickPixelX: 170));
        Assert.False(selection.HasSelection);
    }

    [Fact]
    public void HitTargetsUseCurrentZoomedAxisLimits()
    {
        var roam = Roam(12, "roam-a");
        var fullWindowTarget = Assert.Single(RoamMarkerSelectionModel.CreateHitTargets(
            [roam],
            new GraphAxisLimits(0, 20),
            plotWidthPixels: 200,
            SecondsFromBase));
        var zoomedTarget = Assert.Single(RoamMarkerSelectionModel.CreateHitTargets(
            [roam],
            new GraphAxisLimits(10, 20),
            plotWidthPixels: 200,
            SecondsFromBase));
        var selection = new RoamMarkerSelectionModel();

        Assert.Equal(120, fullWindowTarget.PixelX, precision: 6);
        Assert.Equal(40, zoomedTarget.PixelX, precision: 6);
        Assert.Equal("roam-a", selection.SelectNearest([zoomedTarget], clickPixelX: 42));
    }

    [Fact]
    public void HitTargetsExcludeInvisibleMarkersAndDeduplicateStableIds()
    {
        var targets = RoamMarkerSelectionModel.CreateHitTargets(
            [Roam(5, "roam-a"), Roam(6, "roam-a"), Roam(25, "roam-c")],
            new GraphAxisLimits(0, 20),
            plotWidthPixels: 200,
            SecondsFromBase);

        var target = Assert.Single(targets);
        Assert.Equal("roam-a", target.MarkerId);
        Assert.Equal(BaseTimestamp.AddSeconds(5), target.Timestamp);
    }

    [Fact]
    public void PreviousAndNextWalkChronologicalMarkersAndWrap()
    {
        var roams = new[]
        {
            Roam(20, "roam-c"),
            Roam(10, "roam-b"),
            Roam(5, "roam-a")
        };
        var selection = new RoamMarkerSelectionModel();

        Assert.Equal("roam-a", selection.SelectNext(roams));
        Assert.Equal("roam-b", selection.SelectNext(roams));
        Assert.Equal("roam-a", selection.SelectPrevious(roams));
        Assert.Equal("roam-c", selection.SelectPrevious(roams));
    }

    [Fact]
    public void SelectedRoamDetailsContainOnlyDirectWgbRoamData()
    {
        var details = SelectedRoamDetailsFormatter.Format(Roam(
            seconds: 10,
            markerId: "roam-a",
            oldAp: "ap-a",
            newAp: "ap-b",
            oldBssid: "11:11:11:11:11:11",
            newBssid: "22:22:22:22:22:22",
            oldChannel: "11",
            newChannel: "36",
            oldRadio: "0",
            newRadio: "1",
            oldRssi: "-61",
            newRssi: "-65",
            oldTx: "54.0 Mbps",
            oldRx: "48.0 Mbps",
            newTx: "144.4 Mbps",
            newRx: "130.0 Mbps",
            classification: WgbRoamClassification.DifferentApDifferentChannel));

        Assert.Contains("2026-07-18", details.Observed);
        Assert.Equal("ap-a -> ap-b", details.Ap);
        Assert.Equal("11:11:11:11:11:11 -> 22:22:22:22:22:22", details.Bssid);
        Assert.Equal("11 -> 36", details.Channel);
        Assert.Equal("0 -> 1", details.Radio);
        Assert.Equal("-61 -> -65 dBm", details.Rssi);
        Assert.Equal("54.0/48.0 -> 144.4/130.0 Mbps", details.Rate);
        Assert.Equal("DifferentApDifferentChannel", details.Classification);

        var rendered = string.Join(" ", details.Observed, details.Ap, details.Bssid, details.Channel, details.Radio, details.Rssi, details.Rate, details.Classification, details.Tooltip)
            .ToUpperInvariant();
        Assert.DoesNotContain("ICMP", rendered);
        Assert.DoesNotContain("LOSS", rendered);
        Assert.DoesNotContain("RECOVER", rendered);
        Assert.DoesNotContain("OUTAGE", rendered);
        Assert.DoesNotContain("DURATION", rendered);
    }

    [Fact]
    public void RoamMarkerStatusIsCompactAndDoesNotRenderOldMarkerList()
    {
        var status = RoamMarkerStatusFormatter.Format(roamMarkerCount: 12, hasSelection: true);

        Assert.Equal("12 roam markers - click a marker for details - selected", status);
        Assert.DoesNotContain("Markers:", status);
        Assert.DoesNotContain("|", status);
        Assert.DoesNotContain("ap-a", status);
    }

    [Fact]
    public void RealtimeModelSharesStableMarkerIdBetweenGraphsAndRoamDetails()
    {
        var model = new DiagnosticsRealtimeModel();
        model.Apply(new WgbPollEvent(
            WgbPollEventKind.PollSucceeded,
            BaseTimestamp,
            Association("ap-a", "11:11:11:11:11:11", "11", "0", "-61"),
            ParseResult: null,
            RawOutput: null,
            Message: "poll"));
        model.Apply(new WgbPollEvent(
            WgbPollEventKind.ParentApChanged,
            BaseTimestamp.AddSeconds(5),
            Association("ap-b", "22:22:22:22:22:22", "36", "1", "-65"),
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

        var snapshot = model.Snapshot(BaseTimestamp.AddSeconds(5));
        var marker = Assert.Single(snapshot.Markers.Where(marker => marker.Kind == RealtimeGraphMarkerKind.ParentApChanged));
        var roamEvent = Assert.Single(snapshot.RoamEvents);
        var selection = new RoamMarkerSelectionModel();

        Assert.False(string.IsNullOrWhiteSpace(marker.MarkerId));
        Assert.Equal(marker.MarkerId, roamEvent.MarkerId);
        Assert.Equal("144.4", roamEvent.OldTxRate);
        Assert.Equal("130.0", roamEvent.OldRxRate);
        Assert.Equal("144.4", roamEvent.NewTxRate);
        Assert.Equal("130.0", roamEvent.NewRxRate);

        selection.Select(roamEvent.MarkerId);
        Assert.True(selection.IsSelected(marker.MarkerId));
        Assert.Same(roamEvent, selection.GetSelectedRoam(snapshot.RoamEvents));
    }

    private static double SecondsFromBase(DateTimeOffset timestamp)
    {
        return (timestamp - BaseTimestamp).TotalSeconds;
    }

    private static RealtimeRoamEvent Roam(
        int seconds,
        string markerId,
        string? oldAp = "ap-a",
        string? newAp = "ap-b",
        string? oldBssid = "11:11:11:11:11:11",
        string? newBssid = "22:22:22:22:22:22",
        string? oldChannel = "11",
        string? newChannel = "36",
        string? oldRadio = "0",
        string? newRadio = "1",
        string? oldRssi = "-61",
        string? newRssi = "-65",
        string? oldTx = "54.0",
        string? oldRx = "48.0",
        string? newTx = "144.4",
        string? newRx = "130.0",
        WgbRoamClassification classification = WgbRoamClassification.DifferentApDifferentChannel)
    {
        return new RealtimeRoamEvent(
            BaseTimestamp.AddSeconds(seconds),
            oldAp,
            newAp,
            oldBssid,
            newBssid,
            oldChannel,
            newChannel,
            oldRadio,
            newRadio,
            classification,
            oldRssi,
            newRssi,
            oldTx,
            oldRx,
            newTx,
            newRx,
            markerId);
    }

    private static WgbAssociationSnapshot Association(
        string parentAp,
        string bssid,
        string channel,
        string radioId,
        string rssi)
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
