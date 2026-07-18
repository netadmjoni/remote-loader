using System.Text.RegularExpressions;

namespace WgbDiagnostics.Core.Wgb;

public sealed class WgbAssociationParser : IWgbAssociationParser
{
    private const string ParentApNameField = "Parent AP name";
    private const string ParentBssidField = "Parent BSSID";
    private const string ChannelField = "Channel";
    private const string RssiField = "RSSI";
    private const string RadioIdField = "Radio ID";
    private const string TxRateField = "Tx rate";
    private const string RxRateField = "Rx rate";
    private const string WgbIpField = "WGB IP";
    private const string AssociationStatusField = "Association status";
    private const string CandidateApNameField = "Candidate AP name";
    private const string CandidateBssidField = "Candidate BSSID";

    private static readonly string[] AllFieldNames =
    [
        ParentApNameField,
        ParentBssidField,
        ChannelField,
        RssiField,
        RadioIdField,
        TxRateField,
        RxRateField,
        WgbIpField,
        AssociationStatusField,
        CandidateApNameField,
        CandidateBssidField
    ];

    private static readonly Regex KeyValuePattern = new(
        @"^\s*(?<key>[A-Za-z0-9 /_.-]+?)\s*(?::|=|\s{2,})\s*(?<value>.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public WgbAssociationSnapshot Parse(string? rawOutput)
    {
        return Parse(rawOutput, WgbParserProfiles.Iw9167WgbV1).Association;
    }

    public WgbAssociationParseResult Parse(string? rawOutput, string parserProfile)
    {
        var normalizedProfile = WgbParserProfiles.Normalize(parserProfile);
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return new WgbAssociationParseResult(
                WgbAssociationSnapshot.Unknown,
                normalizedProfile,
                [],
                AllFieldNames,
                []);
        }

        string? parentApName = null;
        string? parentBssid = null;
        string? channel = null;
        string? rssi = null;
        string? radioId = null;
        string? txRate = null;
        string? rxRate = null;
        string? wgbIp = null;
        string associationStatus = "Unknown";
        string? candidateApName = null;
        string? candidateBssid = null;
        var matchedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unclassifiedLines = new List<string>();

        foreach (var rawLine in rawOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmedLine = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }

            var match = KeyValuePattern.Match(rawLine);
            if (!match.Success)
            {
                unclassifiedLines.Add(trimmedLine);
                continue;
            }

            var key = NormalizeKey(match.Groups["key"].Value);
            var value = NormalizeValue(match.Groups["value"].Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                unclassifiedLines.Add(trimmedLine);
                continue;
            }

            var fieldName = ResolveFieldName(key, normalizedProfile);
            switch (fieldName)
            {
                case ParentApNameField:
                    parentApName = value;
                    break;
                case ParentBssidField:
                    parentBssid = value;
                    break;
                case ChannelField:
                    channel = value;
                    break;
                case RssiField:
                    rssi = value;
                    break;
                case RadioIdField:
                    radioId = value;
                    break;
                case TxRateField:
                    txRate = value;
                    break;
                case RxRateField:
                    rxRate = value;
                    break;
                case WgbIpField:
                    wgbIp = value;
                    break;
                case AssociationStatusField:
                    associationStatus = value;
                    break;
                case CandidateApNameField:
                    candidateApName = value;
                    break;
                case CandidateBssidField:
                    candidateBssid = value;
                    break;
                default:
                    unclassifiedLines.Add(trimmedLine);
                    continue;
            }

            matchedFields.Add(fieldName);
        }

        var association = new WgbAssociationSnapshot(
            parentApName,
            parentBssid,
            channel,
            rssi,
            radioId,
            txRate,
            rxRate,
            wgbIp,
            associationStatus,
            candidateApName,
            candidateBssid);

        return new WgbAssociationParseResult(
            association,
            normalizedProfile,
            matchedFields.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            AllFieldNames.Except(matchedFields, StringComparer.OrdinalIgnoreCase).ToArray(),
            unclassifiedLines);
    }

    private static string NormalizeKey(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static string NormalizeValue(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private static string? ResolveFieldName(string normalizedKey, string parserProfile)
    {
        var field = ResolveGenericFieldName(normalizedKey);
        if (field is not null || parserProfile == WgbParserProfiles.GenericKeyValue)
        {
            return field;
        }

        return parserProfile == WgbParserProfiles.Iw9167WgbV1
            ? ResolveIw9167FieldName(normalizedKey)
            : field;
    }

    private static string? ResolveGenericFieldName(string normalizedKey)
    {
        return normalizedKey switch
        {
            "parentapname" or "parentap" or "apname" => ParentApNameField,
            "parentbssid" or "parentapbssid" or "bssid" => ParentBssidField,
            "channel" or "channelnumber" => ChannelField,
            "rssi" or "signalstrength" => RssiField,
            "radioid" or "radio" => RadioIdField,
            "txrate" or "transmitrate" => TxRateField,
            "rxrate" or "receiverate" => RxRateField,
            "wgbip" or "wgbipaddress" or "ipaddress" => WgbIpField,
            "associationstatus" or "assocstatus" or "status" => AssociationStatusField,
            "candidateapname" or "candidateap" => CandidateApNameField,
            "candidatebssid" or "candidateapbssid" => CandidateBssidField,
            _ => null
        };
    }

    private static string? ResolveIw9167FieldName(string normalizedKey)
    {
        return normalizedKey switch
        {
            "parentapmacaddress" or "parentmacaddress" or "parentmac" => ParentBssidField,
            "candidateapmacaddress" or "candidatemacaddress" or "candidatemac" => CandidateBssidField,
            "currentparentap" or "currentap" => ParentApNameField,
            "currentchannel" => ChannelField,
            "currentradioid" => RadioIdField,
            _ => null
        };
    }
}
