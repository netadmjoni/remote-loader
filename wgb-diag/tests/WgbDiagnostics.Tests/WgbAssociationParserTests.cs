using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class WgbAssociationParserTests
{
    private readonly WgbAssociationParser _parser = new();

    [Fact]
    public void ParsesCompleteNormalOutput()
    {
        const string output = """
            Parent AP Name: AP-NORTH-01
            Channel: 44
            RSSI: -62 dBm
            Radio ID: 1
            Tx rate: 144 Mbps
            Rx rate: 130 Mbps
            WGB IP: 10.10.10.25
            Association status: Associated
            """;

        var association = _parser.Parse(output);

        Assert.Equal("AP-NORTH-01", association.ParentApName);
        Assert.Equal("44", association.Channel);
        Assert.Equal("-62 dBm", association.Rssi);
        Assert.Equal("1", association.RadioId);
        Assert.Equal("144 Mbps", association.TxRate);
        Assert.Equal("130 Mbps", association.RxRate);
        Assert.Equal("10.10.10.25", association.WgbIp);
        Assert.Equal("Associated", association.AssociationStatus);
    }

    [Fact]
    public void ParsesIw9167ParentAndCandidateBssidFields()
    {
        const string output = """
            Parent AP Name: AP-IW9167
            Parent AP MAC Address: 0011.2233.4455
            Candidate AP Name: AP-CANDIDATE
            Candidate AP MAC Address: 00aa.bbcc.ddee
            Channel: 100
            """;

        var result = _parser.Parse(output, WgbParserProfiles.Iw9167WgbV1);

        Assert.Equal("AP-IW9167", result.Association.ParentApName);
        Assert.Equal("0011.2233.4455", result.Association.ParentBssid);
        Assert.Equal("AP-CANDIDATE", result.Association.CandidateApName);
        Assert.Equal("00aa.bbcc.ddee", result.Association.CandidateBssid);
        Assert.Contains("Parent BSSID", result.MatchedFields);
        Assert.Contains("Candidate BSSID", result.MatchedFields);
    }

    [Fact]
    public void ParseDiagnosticsReportMatchedMissingAndUnclassifiedRows()
    {
        const string output = """
            Parent AP Name: AP-DIAG
            Channel: 36
            Vendor private line without key value
            Unknown Field: some value
            """;

        var result = _parser.Parse(output, WgbParserProfiles.GenericKeyValue);

        Assert.Contains("Parent AP name", result.MatchedFields);
        Assert.Contains("Channel", result.MatchedFields);
        Assert.Contains("Parent BSSID", result.MissingFields);
        Assert.Contains("Vendor private line without key value", result.UnclassifiedLines);
        Assert.Contains("Unknown Field: some value", result.UnclassifiedLines);
    }

    [Fact]
    public void MissingFieldsRemainNullOrUnknown()
    {
        const string output = """
            Parent AP Name: AP-SOUTH-02
            RSSI: -70 dBm
            """;

        var association = _parser.Parse(output);

        Assert.Equal("AP-SOUTH-02", association.ParentApName);
        Assert.Equal("-70 dBm", association.Rssi);
        Assert.Null(association.Channel);
        Assert.Null(association.RadioId);
        Assert.Equal("Unknown", association.AssociationStatus);
    }

    [Fact]
    public void ParsesExtraWhitespace()
    {
        const string output = """
              Parent AP Name   :    AP-WHITESPACE
              Tx rate          =    54    Mbps
              Rx rate               48    Mbps
              Association status:    Associated
            """;

        var association = _parser.Parse(output);

        Assert.Equal("AP-WHITESPACE", association.ParentApName);
        Assert.Equal("54 Mbps", association.TxRate);
        Assert.Equal("48 Mbps", association.RxRate);
        Assert.Equal("Associated", association.AssociationStatus);
    }

    [Fact]
    public void ParsesChangedFieldOrder()
    {
        const string output = """
            WGB IP: 192.0.2.10
            Association status: Associated
            Rx rate: 72 Mbps
            Radio ID: dot11radio 0
            Parent AP Name: AP-ORDERED
            Channel: 11
            Tx rate: 65 Mbps
            RSSI: -55
            """;

        var association = _parser.Parse(output);

        Assert.Equal("AP-ORDERED", association.ParentApName);
        Assert.Equal("11", association.Channel);
        Assert.Equal("-55", association.Rssi);
        Assert.Equal("dot11radio 0", association.RadioId);
        Assert.Equal("65 Mbps", association.TxRate);
        Assert.Equal("72 Mbps", association.RxRate);
        Assert.Equal("192.0.2.10", association.WgbIp);
    }

    [Fact]
    public void EmptyOutputReturnsUnknownSnapshot()
    {
        var association = _parser.Parse("");

        Assert.Null(association.ParentApName);
        Assert.Null(association.Channel);
        Assert.Equal("Unknown", association.AssociationStatus);
    }

    [Fact]
    public void IgnoresUnknownExtraRows()
    {
        const string output = """
            Header that should be ignored
            Weird field: keep ignoring me
            Parent AP Name: AP-EXTRA
            Another random row without separator
            Channel: 149
            """;

        var association = _parser.Parse(output);

        Assert.Equal("AP-EXTRA", association.ParentApName);
        Assert.Equal("149", association.Channel);
        Assert.Equal("Unknown", association.AssociationStatus);
    }
}
