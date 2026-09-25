using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Realtime;
using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class WgbCompactStatusModelTests
{
    private static readonly DateTimeOffset BaseTimestamp = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompactStatusFormatsCurrentWgbState()
    {
        var realtime = new DiagnosticsRealtimeModel();
        realtime.Apply(new WgbPollEvent(
            WgbPollEventKind.PollSucceeded,
            BaseTimestamp,
            Association("ap-a", "11:11:11:11:11:11", "11", "0", "-61"),
            ParseResult: null,
            RawOutput: null,
            Message: null));

        var compact = WgbCompactStatusModel.FromSnapshot(realtime.Snapshot(BaseTimestamp.AddSeconds(1)));

        Assert.Equal("ap-a", compact.ParentApName);
        Assert.Equal("11:11:11:11:11:11", compact.ParentBssid);
        Assert.Equal("11", compact.Channel);
        Assert.Equal("0", compact.RadioId);
        Assert.Equal("-61 dBm", compact.Rssi);
        Assert.Equal("144.4", compact.TxRate);
        Assert.Equal("130.0", compact.RxRate);
        Assert.Equal("Associated", compact.AssociationStatus);
        Assert.Equal("Poll succeeded", compact.SessionStatus);
    }

    [Fact]
    public void CompactStatusShowsLastRoam()
    {
        var realtime = new DiagnosticsRealtimeModel();
        realtime.Apply(new WgbPollEvent(
            WgbPollEventKind.ParentApChanged,
            BaseTimestamp,
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

        var compact = WgbCompactStatusModel.FromSnapshot(realtime.Snapshot(BaseTimestamp));

        Assert.Contains("ap-a -> ap-b", compact.LastRoam);
        Assert.Contains("ch 11 -> 36", compact.LastRoam);
        Assert.Contains("DifferentApDifferentChannel", compact.LastRoam);
    }

    [Fact]
    public void RoamEventKeepsNewRssiWithoutIcmpDerivedDuration()
    {
        var realtime = new DiagnosticsRealtimeModel();
        realtime.Apply(new WgbPollEvent(
            WgbPollEventKind.ParentApChanged,
            BaseTimestamp.AddMilliseconds(10),
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

        var roamEvent = Assert.Single(realtime.Snapshot(BaseTimestamp.AddMilliseconds(10)).RoamEvents);

        Assert.Equal("-62", roamEvent.NewRssi);
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
