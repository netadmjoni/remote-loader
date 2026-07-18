using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class WgbRoamClassifierTests
{
    [Fact]
    public void ClassifiesApChangeWithChannelChange()
    {
        var classification = WgbRoamClassifier.Classify(
            Snapshot(parentAp: "AP-A", bssid: "0011.2233.0001", channel: "36", radioId: "0"),
            Snapshot(parentAp: "AP-B", bssid: "0011.2233.0002", channel: "100", radioId: "0"));

        Assert.Equal(WgbRoamClassification.DifferentApDifferentChannel, classification);
    }

    [Fact]
    public void ClassifiesApChangeOnSameChannel()
    {
        var classification = WgbRoamClassifier.Classify(
            Snapshot(parentAp: "AP-A", bssid: "0011.2233.0001", channel: "36", radioId: "0"),
            Snapshot(parentAp: "AP-B", bssid: "0011.2233.0002", channel: "36", radioId: "0"));

        Assert.Equal(WgbRoamClassification.DifferentApSameChannel, classification);
    }

    [Fact]
    public void ClassifiesSameApWithRadioIdChange()
    {
        var classification = WgbRoamClassifier.Classify(
            Snapshot(parentAp: "AP-A", bssid: "0011.2233.0001", channel: "36", radioId: "0"),
            Snapshot(parentAp: "AP-A", bssid: "0011.2233.0001", channel: "36", radioId: "1"));

        Assert.Equal(WgbRoamClassification.SameApDifferentRadio, classification);
    }

    [Fact]
    public void MissingBssidDoesNotPreventApSameChannelClassification()
    {
        var classification = WgbRoamClassifier.Classify(
            Snapshot(parentAp: "AP-A", bssid: null, channel: "36", radioId: "0"),
            Snapshot(parentAp: "AP-B", bssid: null, channel: "36", radioId: "0"));

        Assert.Equal(WgbRoamClassification.DifferentApSameChannel, classification);
    }

    [Fact]
    public void IncompleteOutputClassifiesAsUnknown()
    {
        var classification = WgbRoamClassifier.Classify(
            Snapshot(parentAp: "AP-A", bssid: null, channel: null, radioId: null),
            Snapshot(parentAp: "AP-B", bssid: null, channel: null, radioId: null));

        Assert.Equal(WgbRoamClassification.Unknown, classification);
    }

    private static WgbAssociationSnapshot Snapshot(
        string? parentAp,
        string? bssid,
        string? channel,
        string? radioId)
    {
        return new WgbAssociationSnapshot(
            parentAp,
            bssid,
            channel,
            Rssi: null,
            RadioId: radioId,
            TxRate: null,
            RxRate: null,
            WgbIp: null,
            AssociationStatus: "Unknown",
            CandidateApName: null,
            CandidateBssid: null);
    }
}
