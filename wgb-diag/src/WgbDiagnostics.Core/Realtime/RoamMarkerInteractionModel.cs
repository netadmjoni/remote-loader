using System.Globalization;
using WgbDiagnostics.Core.Wgb;

namespace WgbDiagnostics.Core.Realtime;

public sealed record RoamMarkerHitTarget(
    string MarkerId,
    DateTimeOffset Timestamp,
    double PlotX,
    double PixelX);

public sealed record SelectedRoamDetails(
    string Observed,
    string Ap,
    string Bssid,
    string Channel,
    string Radio,
    string Rssi,
    string Rate,
    string Classification)
{
    public string Tooltip => string.Join(
        Environment.NewLine,
        Observed[..Math.Min(19, Observed.Length)],
        Ap,
        $"CH {Channel}",
        $"R {Radio}",
        $"RSSI {Rssi}");
}

public sealed class RoamMarkerSelectionModel
{
    public const double DefaultHitTolerancePixels = 10;

    public string? SelectedMarkerId { get; private set; }

    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectedMarkerId);

    public static IReadOnlyList<RoamMarkerHitTarget> CreateHitTargets(
        IEnumerable<RealtimeRoamEvent> roamEvents,
        GraphAxisLimits xLimits,
        double plotWidthPixels,
        Func<DateTimeOffset, double> timestampToPlotX)
    {
        if (plotWidthPixels <= 0 || xLimits.Width <= 0)
        {
            return [];
        }

        return roamEvents
            .Select(roamEvent => CreateHitTarget(roamEvent, xLimits, plotWidthPixels, timestampToPlotX))
            .Where(target => target is not null)
            .Cast<RoamMarkerHitTarget>()
            .GroupBy(target => target.MarkerId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(target => target.Timestamp).First())
            .ToArray();
    }

    public static RoamMarkerHitTarget? CreateHitTarget(
        RealtimeRoamEvent roamEvent,
        GraphAxisLimits xLimits,
        double plotWidthPixels,
        Func<DateTimeOffset, double> timestampToPlotX)
    {
        if (plotWidthPixels <= 0 || xLimits.Width <= 0)
        {
            return null;
        }

        var plotX = timestampToPlotX(roamEvent.Timestamp);
        if (plotX < xLimits.MinimumX || plotX > xLimits.MaximumX)
        {
            return null;
        }

        var pixelX = (plotX - xLimits.MinimumX) / xLimits.Width * plotWidthPixels;
        return new RoamMarkerHitTarget(GetStableMarkerId(roamEvent), roamEvent.Timestamp, plotX, pixelX);
    }

    public string? SelectNearest(
        IEnumerable<RoamMarkerHitTarget> hitTargets,
        double clickPixelX,
        double hitTolerancePixels = DefaultHitTolerancePixels)
    {
        var nearest = hitTargets
            .Select(target => new
            {
                Target = target,
                Distance = Math.Abs(target.PixelX - clickPixelX)
            })
            .Where(candidate => candidate.Distance <= hitTolerancePixels)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Target.Timestamp)
            .FirstOrDefault();

        SelectedMarkerId = nearest?.Target.MarkerId;
        return SelectedMarkerId;
    }

    public string? SelectPrevious(IEnumerable<RealtimeRoamEvent> roamEvents)
    {
        return SelectRelative(roamEvents, offset: -1);
    }

    public string? SelectNext(IEnumerable<RealtimeRoamEvent> roamEvents)
    {
        return SelectRelative(roamEvents, offset: 1);
    }

    public void Select(string? markerId)
    {
        SelectedMarkerId = string.IsNullOrWhiteSpace(markerId) ? null : markerId;
    }

    public void Clear()
    {
        SelectedMarkerId = null;
    }

    public bool IsSelected(string? markerId)
    {
        return !string.IsNullOrWhiteSpace(markerId)
            && string.Equals(SelectedMarkerId, markerId, StringComparison.Ordinal);
    }

    public RealtimeRoamEvent? GetSelectedRoam(IEnumerable<RealtimeRoamEvent> roamEvents)
    {
        if (!HasSelection)
        {
            return null;
        }

        return roamEvents.FirstOrDefault(roamEvent =>
            string.Equals(GetStableMarkerId(roamEvent), SelectedMarkerId, StringComparison.Ordinal));
    }

    private string? SelectRelative(IEnumerable<RealtimeRoamEvent> roamEvents, int offset)
    {
        var sorted = roamEvents
            .OrderBy(roamEvent => roamEvent.Timestamp)
            .ThenBy(GetStableMarkerId, StringComparer.Ordinal)
            .ToArray();

        if (sorted.Length == 0)
        {
            Clear();
            return null;
        }

        var currentIndex = Array.FindIndex(
            sorted,
            roamEvent => string.Equals(GetStableMarkerId(roamEvent), SelectedMarkerId, StringComparison.Ordinal));
        var nextIndex = currentIndex < 0
            ? offset < 0 ? sorted.Length - 1 : 0
            : (currentIndex + offset + sorted.Length) % sorted.Length;
        SelectedMarkerId = GetStableMarkerId(sorted[nextIndex]);
        return SelectedMarkerId;
    }

    public static string GetStableMarkerId(RealtimeRoamEvent roamEvent)
    {
        if (!string.IsNullOrWhiteSpace(roamEvent.MarkerId))
        {
            return roamEvent.MarkerId;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"roam:{roamEvent.Timestamp.UtcTicks}:{roamEvent.OldParentApName}:{roamEvent.NewParentApName}:{roamEvent.OldParentBssid}:{roamEvent.NewParentBssid}:{roamEvent.OldChannel}:{roamEvent.NewChannel}:{roamEvent.OldRadioId}:{roamEvent.NewRadioId}:{roamEvent.RoamClassification}");
    }
}

public static class RoamMarkerStatusFormatter
{
    public static string Format(int roamMarkerCount, bool hasSelection)
    {
        var suffix = hasSelection ? " - selected" : "";
        return roamMarkerCount == 1
            ? $"1 roam marker - click a marker for details{suffix}"
            : $"{roamMarkerCount.ToString(CultureInfo.InvariantCulture)} roam markers - click a marker for details{suffix}";
    }
}

public static class SelectedRoamDetailsFormatter
{
    public static SelectedRoamDetails Format(RealtimeRoamEvent roamEvent)
    {
        return new SelectedRoamDetails(
            WgbAssociationSample.FormatTimestamp(roamEvent.Timestamp),
            $"{FormatOptional(roamEvent.OldParentApName)} -> {FormatOptional(roamEvent.NewParentApName)}",
            $"{FormatOptional(roamEvent.OldParentBssid)} -> {FormatOptional(roamEvent.NewParentBssid)}",
            $"{FormatOptional(roamEvent.OldChannel)} -> {FormatOptional(roamEvent.NewChannel)}",
            $"{FormatOptional(roamEvent.OldRadioId)} -> {FormatOptional(roamEvent.NewRadioId)}",
            FormatRssiTransition(roamEvent.OldRssi, roamEvent.NewRssi),
            FormatRateTransition(roamEvent.OldTxRate, roamEvent.OldRxRate, roamEvent.NewTxRate, roamEvent.NewRxRate),
            roamEvent.RoamClassification.ToString());
    }

    private static string FormatOptional(string? value)
    {
        return WgbAssociationSample.FormatOptional(value);
    }

    private static string FormatRssiTransition(string? oldValue, string? newValue)
    {
        var oldRssi = WgbRssiNormalizer.NormalizeForDisplay(oldValue);
        var newRssi = WgbRssiNormalizer.NormalizeForDisplay(newValue);
        if (oldRssi is null && newRssi is null)
        {
            return "-";
        }

        return $"{oldRssi ?? "-"} -> {newRssi ?? "-"} dBm";
    }

    private static string FormatRateTransition(
        string? oldTxRate,
        string? oldRxRate,
        string? newTxRate,
        string? newRxRate)
    {
        var oldPair = FormatRatePair(oldTxRate, oldRxRate);
        var newPair = FormatRatePair(newTxRate, newRxRate);
        if (oldPair == "-/-" && newPair == "-/-")
        {
            return "-";
        }

        return $"{oldPair} -> {newPair} Mbps";
    }

    private static string FormatRatePair(string? txRate, string? rxRate)
    {
        var tx = WgbAssociationSample.FormatOptional(WgbAssociationSample.NormalizeRateMbps(txRate));
        var rx = WgbAssociationSample.FormatOptional(WgbAssociationSample.NormalizeRateMbps(rxRate));
        return $"{tx}/{rx}";
    }
}
