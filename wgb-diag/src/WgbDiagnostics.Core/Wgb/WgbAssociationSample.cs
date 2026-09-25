using System.Globalization;
using System.Text.RegularExpressions;

namespace WgbDiagnostics.Core.Wgb;

public sealed record WgbAssociationSample(
    DateTimeOffset Timestamp,
    string ParentAp,
    string? ParentBssid,
    string? CandidateAp,
    string? CandidateBssid,
    string? Rssi,
    string? Channel,
    string? TxRateMbps,
    string? RxRateMbps,
    string? RadioId,
    string AssociationState,
    string PollStatus,
    string? ErrorReason)
{
    public static bool TryCreate(WgbPollEvent pollEvent, out WgbAssociationSample? sample)
    {
        sample = null;
        if (pollEvent.Kind != WgbPollEventKind.PollSucceeded || pollEvent.Association is null)
        {
            return false;
        }

        return TryCreate(
            pollEvent.Timestamp,
            pollEvent.Association,
            pollStatus: "OK",
            errorReason: pollEvent.Message,
            out sample);
    }

    public static bool TryCreate(
        DateTimeOffset timestamp,
        WgbAssociationSnapshot association,
        string pollStatus,
        string? errorReason,
        out WgbAssociationSample? sample)
    {
        sample = null;
        if (string.IsNullOrWhiteSpace(association.ParentApName))
        {
            return false;
        }

        sample = new WgbAssociationSample(
            timestamp,
            association.ParentApName.Trim(),
            NormalizeOptionalText(association.ParentBssid),
            NormalizeOptionalText(association.CandidateApName),
            NormalizeOptionalText(association.CandidateBssid),
            WgbRssiNormalizer.NormalizeForDisplay(association.Rssi),
            NormalizeOptionalText(association.Channel),
            NormalizeRateMbps(association.TxRate),
            NormalizeRateMbps(association.RxRate),
            NormalizeOptionalText(association.RadioId),
            string.IsNullOrWhiteSpace(association.AssociationStatus)
                ? "Unknown"
                : association.AssociationStatus.Trim(),
            string.IsNullOrWhiteSpace(pollStatus) ? "Unknown" : pollStatus.Trim(),
            NormalizeOptionalText(errorReason));
        return true;
    }

    public string FormatOperatorRow()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{FormatTimestamp(Timestamp)} AP={ParentAp} RSSI={FormatRssi(Rssi)} CH={FormatOptional(Channel)} RATE=\"{FormatRatePair()}\" R={FormatOptional(RadioId)}");
    }

    public string FormatRoamRow(
        WgbAssociationSample previous,
        WgbRoamClassification classification)
    {
        // Accurate roam timing requires continuous WGB event-log collection and is outside the scope of association polling.
        var details = new List<string>
        {
            $"{FormatTimestamp(Timestamp)} ROAM",
            $"AP={FormatOptional(previous.ParentAp)}->{FormatOptional(ParentAp)}",
            $"BSSID={FormatOptional(previous.ParentBssid)}->{FormatOptional(ParentBssid)}",
            $"CH={FormatOptional(previous.Channel)}->{FormatOptional(Channel)}",
            $"R={FormatOptional(previous.RadioId)}->{FormatOptional(RadioId)}",
            $"RSSI={FormatRssiTransition(previous.Rssi, Rssi)}",
            $"RATE=\"{previous.FormatRatePairForTransition()}->{FormatRatePairForTransition()} Mbps\""
        };

        if (classification != WgbRoamClassification.Unknown)
        {
            details.Add($"CLASS={classification}");
        }

        return string.Join(" ", details);
    }

    public bool HasOperatorChangeFrom(WgbAssociationSample previous)
    {
        return HasComparableChanged(previous.ParentAp, ParentAp)
            || HasComparableChanged(previous.ParentBssid, ParentBssid)
            || HasComparableChanged(previous.Channel, Channel)
            || HasComparableChanged(previous.RadioId, RadioId)
            || HasComparableChanged(previous.AssociationState, AssociationState);
    }

    public bool HasRoamTransitionFrom(WgbAssociationSample previous)
    {
        return HasComparableChanged(previous.ParentAp, ParentAp)
            || HasComparableChanged(previous.ParentBssid, ParentBssid)
            || HasComparableChanged(previous.Channel, Channel)
            || HasComparableChanged(previous.RadioId, RadioId);
    }

    public WgbAssociationSnapshot ToAssociationSnapshot()
    {
        return new WgbAssociationSnapshot(
            ParentAp,
            ParentBssid,
            Channel,
            Rssi,
            RadioId,
            TxRateMbps,
            RxRateMbps,
            WgbIp: null,
            AssociationStatus: AssociationState,
            CandidateApName: CandidateAp,
            CandidateBssid: CandidateBssid);
    }

    public static string NormalizeRateMbps(string? value)
    {
        var text = NormalizeOptionalText(value);
        if (text is null)
        {
            return "";
        }

        text = Regex.Replace(text, @"\s*(?:mbps|mb/s)\s*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text.Trim();
    }

    public static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    public static string FormatOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    public static string FormatRssi(string? value)
    {
        var normalized = WgbRssiNormalizer.NormalizeForDisplay(value);
        return normalized is null ? "-" : $"{normalized} dBm";
    }

    private string FormatRatePair()
    {
        var tx = FormatOptional(TxRateMbps);
        var rx = FormatOptional(RxRateMbps);
        return tx == "-" && rx == "-"
            ? "-/-"
            : $"{tx}/{rx} Mbps";
    }

    private string FormatRatePairForTransition()
    {
        var tx = FormatOptional(TxRateMbps);
        var rx = FormatOptional(RxRateMbps);
        return tx == "-" && rx == "-"
            ? "-/-"
            : $"{tx}/{rx}";
    }

    private static string FormatRssiTransition(string? oldValue, string? newValue)
    {
        var oldRssi = WgbRssiNormalizer.NormalizeForDisplay(oldValue);
        var newRssi = WgbRssiNormalizer.NormalizeForDisplay(newValue);
        if (oldRssi is null && newRssi is null)
        {
            return "-";
        }

        return $"{oldRssi ?? "-"}->{newRssi ?? "-"} dBm";
    }

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool HasComparableChanged(string? oldValue, string? newValue)
    {
        return !string.IsNullOrWhiteSpace(oldValue)
            && !string.IsNullOrWhiteSpace(newValue)
            && !StringComparer.OrdinalIgnoreCase.Equals(oldValue.Trim(), newValue.Trim());
    }
}
