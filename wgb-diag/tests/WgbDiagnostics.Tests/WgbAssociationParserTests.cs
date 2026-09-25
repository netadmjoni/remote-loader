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
        Assert.Equal("-62", association.Rssi);
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
    public void ParsesReferenceScriptLabelsForIw9167Output()
    {
        const string output = """
            WGB#show wgb dot11 associations
            Parent AP Name             : MHO1109STV-221-bs218
            Parent AP MAC              : 0011.2233.4455
            RSSI                       : 56
            Channel                    : 44
            Current Datarate (Tx/Rx)   : 173/144 Mbps
            Uplink Radio ID            : 1
            Connected Duration         : 00:01:23
            Uplink State               : Associated
            Auth Type                  : WPA2
            Key management Type        : PSK
            WGB#
            """;

        var result = _parser.Parse(output, WgbParserProfiles.Iw9167WgbV1);
        var association = result.Association;

        Assert.Equal("MHO1109STV-221-bs218", association.ParentApName);
        Assert.Equal("0011.2233.4455", association.ParentBssid);
        Assert.Equal("-56", association.Rssi);
        Assert.Equal("44", association.Channel);
        Assert.Equal("173 Mbps", association.TxRate);
        Assert.Equal("144 Mbps", association.RxRate);
        Assert.Equal("1", association.RadioId);
        Assert.Equal("Associated", association.AssociationStatus);
        Assert.Equal("00:01:23", association.ConnectedDuration);
        Assert.Equal("WPA2", association.AuthType);
        Assert.Equal("PSK", association.KeyManagementType);
        Assert.Contains(result.Warnings, warning => warning.Contains("normalized to -56 dBm", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Tx rate", result.MatchedFields);
        Assert.Contains("Rx rate", result.MatchedFields);
        Assert.DoesNotContain(result.UnclassifiedLines, line => line.Contains("show wgb dot11 associations", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StripsAnsiAndControlCharactersBeforeParsing()
    {
        const string output = "\u001b[32mParent AP Name\u001b[0m: AP-COLOR\r\nUplink Radio ID:\u0001 2\r\nCurrent Datarate (Tx/Rx): 144 / 173 Mbps\r\n";

        var association = _parser.Parse(output, WgbParserProfiles.Iw9167WgbV1).Association;

        Assert.Equal("AP-COLOR", association.ParentApName);
        Assert.Equal("2", association.RadioId);
        Assert.Equal("144 Mbps", association.TxRate);
        Assert.Equal("173 Mbps", association.RxRate);
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
        Assert.Equal("-70", association.Rssi);
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

    [Theory]
    [InlineData("RSSI: -56", "-56")]
    [InlineData("RSSI=-94", "-94")]
    [InlineData("  RSSI     :      -75    dBm  ", "-75")]
    public void PreservesSignedRssiValues(string line, string expected)
    {
        var association = _parser.Parse($"Parent AP Name: AP-RSSI\r\n{line}", WgbParserProfiles.Iw9167WgbV1).Association;

        Assert.Equal(expected, association.Rssi);
    }

    [Fact]
    public void GenericProfileDoesNotSilentlyAcceptPositiveRssiAsDbm()
    {
        const string output = """
            AP=MGN1080STV-223
            RSSI=45
            channel=11
            """;

        var result = _parser.Parse(output, WgbParserProfiles.GenericKeyValue);

        Assert.Equal("MGN1080STV-223", result.Association.ParentApName);
        Assert.Null(result.Association.Rssi);
        Assert.Contains(result.Warnings, warning => warning.Contains("no negative sign", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PositiveSignedRssiCreatesDiagnosticAndIsIgnored()
    {
        var result = _parser.Parse("Parent AP Name: AP-BAD\r\nRSSI: +94 dBm", WgbParserProfiles.Iw9167WgbV1);

        Assert.Null(result.Association.Rssi);
        Assert.Contains(result.Warnings, warning => warning.Contains("positive dBm", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void GenericProfileTreatsApAliasAsParentApName()
    {
        const string output = """
            AP=MGN1080STV-223
            RSSI=-45
            channel=11
            """;

        var result = _parser.Parse(output, WgbParserProfiles.GenericKeyValue);

        Assert.Equal("MGN1080STV-223", result.Association.ParentApName);
        Assert.Equal("-45", result.Association.Rssi);
        Assert.Equal("11", result.Association.Channel);
    }
}
