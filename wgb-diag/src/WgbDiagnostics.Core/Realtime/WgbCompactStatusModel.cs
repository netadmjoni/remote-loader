using WgbDiagnostics.Core.Wgb;

namespace WgbDiagnostics.Core.Realtime;

public sealed record WgbCompactStatusModel(
    string ParentApName,
    string ParentBssid,
    string Channel,
    string RadioId,
    string Rssi,
    string TxRate,
    string RxRate,
    string AssociationStatus,
    string LastRoam,
    string LastSuccessfulPoll,
    string DataAge,
    string SessionStatus)
{
    public static WgbCompactStatusModel FromSnapshot(DiagnosticsRealtimeSnapshot snapshot)
    {
        var status = snapshot.WgbStatus;
        var lastRoam = snapshot.RoamEvents.LastOrDefault();

        return new WgbCompactStatusModel(
            Format(status.ParentApName),
            Format(status.ParentBssid),
            Format(status.Channel),
            Format(status.RadioId),
            WgbAssociationSample.FormatRssi(status.Rssi),
            Format(status.TxRate),
            Format(status.RxRate),
            string.IsNullOrWhiteSpace(status.AssociationStatus) ? "Unknown" : status.AssociationStatus,
            lastRoam is null ? "-" : $"{Format(lastRoam.OldParentApName)} -> {Format(lastRoam.NewParentApName)} ch {Format(lastRoam.OldChannel)} -> {Format(lastRoam.NewChannel)} {lastRoam.RoamClassification}",
            status.LastSuccessfulPollTimestamp is null ? "-" : status.LastSuccessfulPollTimestamp.Value.ToLocalTime().ToString("HH:mm:ss"),
            status.LastSuccessfulPollTimestamp is null ? "-" : FormatDuration(status.DataAge),
            status.IsStale ? "Stale" : status.Status);
    }

    private static string Format(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMilliseconds < 1000)
        {
            return $"{duration.TotalMilliseconds:0} ms";
        }

        return duration.TotalMinutes < 1
            ? $"{duration.TotalSeconds:0.0} s"
            : duration.ToString(@"hh\:mm\:ss");
    }
}
