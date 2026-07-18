using System.Globalization;
using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Wgb;

namespace WgbDiagnostics.Core.Realtime;

public sealed class DiagnosticsRealtimeModel
{
    private readonly object _sync = new();
    private readonly List<PingObservation> _pingObservations = [];
    private readonly List<RssiObservation> _rssiObservations = [];
    private readonly List<RealtimeGraphMarker> _markers = [];
    private readonly List<RealtimeRoamEvent> _roamEvents = [];
    private RealtimeGraphOptions _options;
    private PingRealtimeStatus _pingStatus = PingRealtimeStatus.Empty;
    private WgbRealtimeStatus _wgbStatus = WgbRealtimeStatus.Empty;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _latestTimestamp;

    public DiagnosticsRealtimeModel(RealtimeGraphOptions? options = null)
    {
        _options = options ?? RealtimeGraphOptions.Default;
    }

    public void Configure(RealtimeGraphOptions options)
    {
        lock (_sync)
        {
            _options = options;
            TrimToWindow(GetTrimAnchor());
        }
    }

    public void ClearGraph()
    {
        lock (_sync)
        {
            _pingObservations.Clear();
            _rssiObservations.Clear();
            _markers.Clear();
            _roamEvents.Clear();
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _pingObservations.Clear();
            _rssiObservations.Clear();
            _markers.Clear();
            _roamEvents.Clear();
            _pingStatus = PingRealtimeStatus.Empty;
            _wgbStatus = WgbRealtimeStatus.Empty;
            _startedAt = null;
            _latestTimestamp = null;
        }
    }

    public void Apply(IcmpMonitorEvent monitorEvent)
    {
        lock (_sync)
        {
            NoteTimestamp(monitorEvent.Timestamp);
            ApplyPingStatus(monitorEvent);

            switch (monitorEvent.Kind)
            {
                case IcmpMonitorEventKind.PingReply when monitorEvent.RoundTripTime is not null:
                    InsertObservation(PingObservation.Success(
                        monitorEvent.Timestamp,
                        monitorEvent.RoundTripTime.Value.TotalMilliseconds));
                    break;
                case IcmpMonitorEventKind.LossStarted:
                    InsertObservation(PingObservation.Gap(monitorEvent.Timestamp));
                    InsertMarker(new RealtimeGraphMarker(
                        monitorEvent.Timestamp,
                        RealtimeGraphMarkerKind.LossStarted,
                        "LOSS_START"));
                    break;
                case IcmpMonitorEventKind.Loss:
                case IcmpMonitorEventKind.Error:
                    InsertObservation(PingObservation.Gap(monitorEvent.Timestamp));
                    break;
                case IcmpMonitorEventKind.Recovered:
                    InsertMarker(new RealtimeGraphMarker(
                        monitorEvent.Timestamp,
                        RealtimeGraphMarkerKind.Recovered,
                        "RECOVER"));
                    break;
            }

            TrimToWindow(GetTrimAnchor());
        }
    }

    public void Apply(WgbPollEvent pollEvent)
    {
        lock (_sync)
        {
            NoteTimestamp(pollEvent.Timestamp);
            var previousRssi = _wgbStatus.Rssi;
            ApplyWgbStatus(pollEvent);

            if (pollEvent.Association is not null
                && ShouldRecordRssi(pollEvent.Kind)
                && TryParseRssi(pollEvent.Association.Rssi, out var rssi))
            {
                InsertOrReplaceRssiObservation(new RssiObservation(pollEvent.Timestamp, rssi));
            }

            if (pollEvent.Kind == WgbPollEventKind.ParentApChanged)
            {
                InsertMarker(new RealtimeGraphMarker(
                    pollEvent.Timestamp,
                    RealtimeGraphMarkerKind.ParentApChanged,
                    "ParentApChanged",
                    pollEvent.OldParentApName,
                    pollEvent.NewParentApName,
                    pollEvent.OldParentBssid,
                    pollEvent.NewParentBssid,
                    pollEvent.OldChannel,
                    pollEvent.NewChannel,
                    pollEvent.OldRadioId,
                    pollEvent.NewRadioId,
                    pollEvent.RoamClassification));

                InsertRoamEvent(new RealtimeRoamEvent(
                    pollEvent.Timestamp,
                    pollEvent.OldParentApName,
                    pollEvent.NewParentApName,
                    pollEvent.OldParentBssid,
                    pollEvent.NewParentBssid,
                    pollEvent.OldChannel,
                    pollEvent.NewChannel,
                    pollEvent.OldRadioId,
                    pollEvent.NewRadioId,
                    pollEvent.RoamClassification,
                    previousRssi));
            }

            TrimToWindow(GetTrimAnchor());
        }
    }

    public DiagnosticsRealtimeSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_sync)
        {
            TrimToWindow(GetTrimAnchor(now));
            var runtime = _startedAt is null
                ? TimeSpan.Zero
                : now - _startedAt.Value;
            if (runtime < TimeSpan.Zero)
            {
                runtime = TimeSpan.Zero;
            }

            return new DiagnosticsRealtimeSnapshot(
                BuildSegments(),
                BuildRssiPoints(),
                _markers.ToArray(),
                _roamEvents.ToArray(),
                _pingStatus with { Runtime = runtime },
                _wgbStatus,
                _options);
        }
    }

    private void ApplyPingStatus(IcmpMonitorEvent monitorEvent)
    {
        var status = _pingStatus;

        status = monitorEvent.Kind switch
        {
            IcmpMonitorEventKind.PingReply => status with
            {
                CurrentRoundTripTime = monitorEvent.RoundTripTime,
                TotalOk = status.TotalOk + 1,
                ConsecutiveLoss = 0,
                CurrentLossWindow = TimeSpan.Zero,
                CurrentLossStartedAt = null,
                Status = "OK"
            },
            IcmpMonitorEventKind.LossStarted => status with
            {
                CurrentRoundTripTime = null,
                TotalLost = status.TotalLost + 1,
                ConsecutiveLoss = monitorEvent.ConsecutiveLoss,
                CurrentLossWindow = TimeSpan.FromMilliseconds(monitorEvent.EstimatedLossWindowMilliseconds),
                CurrentLossStartedAt = monitorEvent.Timestamp,
                Status = "Loss"
            },
            IcmpMonitorEventKind.Loss => status with
            {
                CurrentRoundTripTime = null,
                TotalLost = status.TotalLost + 1,
                ConsecutiveLoss = monitorEvent.ConsecutiveLoss,
                CurrentLossWindow = TimeSpan.FromMilliseconds(monitorEvent.EstimatedLossWindowMilliseconds),
                CurrentLossStartedAt = status.CurrentLossStartedAt ?? monitorEvent.Timestamp,
                Status = "Loss"
            },
            IcmpMonitorEventKind.AlertThresholdReached => status with
            {
                CurrentRoundTripTime = null,
                ConsecutiveLoss = monitorEvent.ConsecutiveLoss,
                CurrentLossWindow = TimeSpan.FromMilliseconds(monitorEvent.EstimatedLossWindowMilliseconds),
                CurrentLossStartedAt = status.CurrentLossStartedAt ?? monitorEvent.Timestamp,
                Status = "Alert"
            },
            IcmpMonitorEventKind.Recovered => status with
            {
                CurrentRoundTripTime = monitorEvent.RoundTripTime,
                ConsecutiveLoss = 0,
                CurrentLossWindow = TimeSpan.Zero,
                CurrentLossStartedAt = null,
                Status = "Recovered"
            },
            IcmpMonitorEventKind.Error => status with
            {
                CurrentRoundTripTime = null,
                ConsecutiveLoss = monitorEvent.ConsecutiveLoss,
                CurrentLossWindow = TimeSpan.FromMilliseconds(monitorEvent.EstimatedLossWindowMilliseconds),
                CurrentLossStartedAt = status.CurrentLossStartedAt ?? monitorEvent.Timestamp,
                Status = "Error"
            },
            _ => status
        };

        if (monitorEvent.EstimatedLossWindowMilliseconds > status.LongestOutage.TotalMilliseconds)
        {
            status = status with
            {
                LongestOutage = TimeSpan.FromMilliseconds(monitorEvent.EstimatedLossWindowMilliseconds)
            };
        }

        _pingStatus = status;
    }

    private void ApplyWgbStatus(WgbPollEvent pollEvent)
    {
        var status = _wgbStatus with
        {
            Status = pollEvent.Kind switch
            {
                WgbPollEventKind.Connected => "Connected",
                WgbPollEventKind.Disconnected => "Disconnected",
                WgbPollEventKind.PollSucceeded => "Poll succeeded",
                WgbPollEventKind.PollFailed => "Poll failed",
                WgbPollEventKind.AssociationUpdated => "Association updated",
                WgbPollEventKind.ParentApChanged => $"Roam: {pollEvent.RoamClassification}",
                _ => _wgbStatus.Status
            }
        };

        if (pollEvent.Association is not null)
        {
            status = status with
            {
                ParentApName = pollEvent.Association.ParentApName,
                ParentBssid = pollEvent.Association.ParentBssid,
                Channel = pollEvent.Association.Channel,
                RadioId = pollEvent.Association.RadioId,
                Rssi = pollEvent.Association.Rssi,
                TxRate = pollEvent.Association.TxRate,
                RxRate = pollEvent.Association.RxRate,
                AssociationStatus = pollEvent.Association.AssociationStatus
            };
        }

        _wgbStatus = status;
    }

    private static bool ShouldRecordRssi(WgbPollEventKind kind)
    {
        return kind is WgbPollEventKind.PollSucceeded
            or WgbPollEventKind.AssociationUpdated
            or WgbPollEventKind.ParentApChanged;
    }

    private static bool TryParseRssi(string? value, out double rssi)
    {
        rssi = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value
            .Trim()
            .Replace("dBm", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out rssi)
            || double.TryParse(normalized, NumberStyles.Float, CultureInfo.CurrentCulture, out rssi);
    }

    private void NoteTimestamp(DateTimeOffset timestamp)
    {
        _startedAt ??= timestamp;
        if (_latestTimestamp is null || timestamp > _latestTimestamp.Value)
        {
            _latestTimestamp = timestamp;
        }
    }

    private DateTimeOffset GetTrimAnchor(DateTimeOffset? now = null)
    {
        if (now is not null && (_latestTimestamp is null || now.Value > _latestTimestamp.Value))
        {
            return now.Value;
        }

        return _latestTimestamp ?? now ?? DateTimeOffset.UtcNow;
    }

    private void InsertObservation(PingObservation observation)
    {
        var index = _pingObservations.BinarySearch(observation, PingObservationTimestampComparer.Instance);
        if (index < 0)
        {
            index = ~index;
        }

        _pingObservations.Insert(index, observation);
    }

    private void InsertOrReplaceRssiObservation(RssiObservation observation)
    {
        var index = _rssiObservations.BinarySearch(observation, RssiObservationTimestampComparer.Instance);
        if (index >= 0)
        {
            _rssiObservations[index] = observation;
            return;
        }

        _rssiObservations.Insert(~index, observation);
    }

    private void InsertMarker(RealtimeGraphMarker marker)
    {
        if (_markers.Contains(marker))
        {
            return;
        }

        var index = _markers.BinarySearch(marker, RealtimeGraphMarkerTimestampComparer.Instance);
        if (index < 0)
        {
            index = ~index;
        }

        _markers.Insert(index, marker);
    }

    private void InsertRoamEvent(RealtimeRoamEvent roamEvent)
    {
        if (_roamEvents.Any(existing => IsSameRoamEvent(existing, roamEvent)))
        {
            return;
        }

        var index = _roamEvents.BinarySearch(roamEvent, RealtimeRoamEventTimestampComparer.Instance);
        if (index < 0)
        {
            index = ~index;
        }

        _roamEvents.Insert(index, roamEvent);
    }

    private static bool IsSameRoamEvent(
        RealtimeRoamEvent left,
        RealtimeRoamEvent right)
    {
        return left.Timestamp == right.Timestamp
            && left.OldParentApName == right.OldParentApName
            && left.NewParentApName == right.NewParentApName
            && left.OldParentBssid == right.OldParentBssid
            && left.NewParentBssid == right.NewParentBssid
            && left.OldChannel == right.OldChannel
            && left.NewChannel == right.NewChannel
            && left.OldRadioId == right.OldRadioId
            && left.NewRadioId == right.NewRadioId
            && left.RoamClassification == right.RoamClassification;
    }

    private IReadOnlyList<RttGraphSegment> BuildSegments()
    {
        var segments = new List<RttGraphSegment>();
        var current = new List<RttGraphPoint>();

        foreach (var observation in _pingObservations)
        {
            if (!observation.IsSuccess)
            {
                FlushCurrentSegment();
                continue;
            }

            current.Add(new RttGraphPoint(observation.Timestamp, observation.RoundTripTimeMilliseconds!.Value));
        }

        FlushCurrentSegment();
        return segments;

        void FlushCurrentSegment()
        {
            if (current.Count == 0)
            {
                return;
            }

            segments.Add(new RttGraphSegment(current.ToArray()));
            current.Clear();
        }
    }

    private IReadOnlyList<RssiGraphPoint> BuildRssiPoints()
    {
        return _rssiObservations
            .Select(observation => new RssiGraphPoint(observation.Timestamp, observation.Rssi))
            .ToArray();
    }

    private void TrimToWindow(DateTimeOffset anchor)
    {
        var cutoff = anchor - _options.VisibleWindow;
        _pingObservations.RemoveAll(observation => observation.Timestamp < cutoff);
        _rssiObservations.RemoveAll(observation => observation.Timestamp < cutoff);
        _markers.RemoveAll(marker => marker.Timestamp < cutoff);
        _roamEvents.RemoveAll(roamEvent => roamEvent.Timestamp < cutoff);

        TrimOldest(_pingObservations, _options.MaxDataPoints);
        TrimOldest(_rssiObservations, _options.MaxDataPoints);
        TrimOldest(_markers, _options.MaxMarkers);
        TrimOldest(_roamEvents, _options.MaxMarkers);
    }

    private static void TrimOldest<T>(List<T> items, int maxItems)
    {
        if (items.Count <= maxItems)
        {
            return;
        }

        items.RemoveRange(0, items.Count - maxItems);
    }

    private sealed record PingObservation(
        DateTimeOffset Timestamp,
        double? RoundTripTimeMilliseconds,
        bool IsSuccess)
    {
        public static PingObservation Success(DateTimeOffset timestamp, double roundTripTimeMilliseconds)
        {
            return new PingObservation(timestamp, roundTripTimeMilliseconds, IsSuccess: true);
        }

        public static PingObservation Gap(DateTimeOffset timestamp)
        {
            return new PingObservation(timestamp, RoundTripTimeMilliseconds: null, IsSuccess: false);
        }
    }

    private sealed record RssiObservation(DateTimeOffset Timestamp, double Rssi);

    private sealed class PingObservationTimestampComparer : IComparer<PingObservation>
    {
        public static PingObservationTimestampComparer Instance { get; } = new();

        public int Compare(PingObservation? x, PingObservation? y)
        {
            return Nullable.Compare(x?.Timestamp, y?.Timestamp);
        }
    }

    private sealed class RssiObservationTimestampComparer : IComparer<RssiObservation>
    {
        public static RssiObservationTimestampComparer Instance { get; } = new();

        public int Compare(RssiObservation? x, RssiObservation? y)
        {
            return Nullable.Compare(x?.Timestamp, y?.Timestamp);
        }
    }

    private sealed class RealtimeGraphMarkerTimestampComparer : IComparer<RealtimeGraphMarker>
    {
        public static RealtimeGraphMarkerTimestampComparer Instance { get; } = new();

        public int Compare(RealtimeGraphMarker? x, RealtimeGraphMarker? y)
        {
            return Nullable.Compare(x?.Timestamp, y?.Timestamp);
        }
    }

    private sealed class RealtimeRoamEventTimestampComparer : IComparer<RealtimeRoamEvent>
    {
        public static RealtimeRoamEventTimestampComparer Instance { get; } = new();

        public int Compare(RealtimeRoamEvent? x, RealtimeRoamEvent? y)
        {
            return Nullable.Compare(x?.Timestamp, y?.Timestamp);
        }
    }
}

public sealed record RealtimeGraphOptions(
    TimeSpan VisibleWindow,
    int MaxDataPoints,
    int MaxMarkers)
{
    public static RealtimeGraphOptions Default { get; } = new(
        TimeSpan.FromMinutes(60),
        MaxDataPoints: 36_000,
        MaxMarkers: 2_000);

    public static RealtimeGraphOptions FromDiagnosticsOptions(WgbDiagnosticsOptions options)
    {
        var minutes = Math.Max(1, options.GraphVisibleMinutes);
        return new RealtimeGraphOptions(
            TimeSpan.FromMinutes(minutes),
            MaxDataPoints: Math.Max(600, minutes * 60 * 20),
            MaxMarkers: Math.Max(200, minutes * 20));
    }
}

public sealed record DiagnosticsRealtimeSnapshot(
    IReadOnlyList<RttGraphSegment> RttSegments,
    IReadOnlyList<RssiGraphPoint> RssiPoints,
    IReadOnlyList<RealtimeGraphMarker> Markers,
    IReadOnlyList<RealtimeRoamEvent> RoamEvents,
    PingRealtimeStatus PingStatus,
    WgbRealtimeStatus WgbStatus,
    RealtimeGraphOptions Options);

public sealed record RttGraphSegment(IReadOnlyList<RttGraphPoint> Points);

public sealed record RttGraphPoint(DateTimeOffset Timestamp, double RoundTripTimeMilliseconds);

public sealed record RssiGraphPoint(DateTimeOffset Timestamp, double Rssi);

public sealed record PingRealtimeStatus(
    TimeSpan? CurrentRoundTripTime,
    long TotalOk,
    long TotalLost,
    int ConsecutiveLoss,
    TimeSpan LongestOutage,
    TimeSpan Runtime,
    TimeSpan CurrentLossWindow,
    DateTimeOffset? CurrentLossStartedAt,
    string Status)
{
    public static PingRealtimeStatus Empty { get; } = new(
        CurrentRoundTripTime: null,
        TotalOk: 0,
        TotalLost: 0,
        ConsecutiveLoss: 0,
        LongestOutage: TimeSpan.Zero,
        Runtime: TimeSpan.Zero,
        CurrentLossWindow: TimeSpan.Zero,
        CurrentLossStartedAt: null,
        Status: "Stopped");
}

public sealed record WgbRealtimeStatus(
    string? ParentApName,
    string? ParentBssid,
    string? Channel,
    string? RadioId,
    string? Rssi,
    string? TxRate,
    string? RxRate,
    string AssociationStatus,
    string Status)
{
    public static WgbRealtimeStatus Empty { get; } = new(
        ParentApName: null,
        ParentBssid: null,
        Channel: null,
        RadioId: null,
        Rssi: null,
        TxRate: null,
        RxRate: null,
        AssociationStatus: "Unknown",
        Status: "Not tested");
}

public sealed record RealtimeGraphMarker(
    DateTimeOffset Timestamp,
    RealtimeGraphMarkerKind Kind,
    string Label,
    string? OldParentApName = null,
    string? NewParentApName = null,
    string? OldParentBssid = null,
    string? NewParentBssid = null,
    string? OldChannel = null,
    string? NewChannel = null,
    string? OldRadioId = null,
    string? NewRadioId = null,
    WgbRoamClassification? RoamClassification = null);

public sealed record RealtimeRoamEvent(
    DateTimeOffset Timestamp,
    string? OldParentApName,
    string? NewParentApName,
    string? OldParentBssid,
    string? NewParentBssid,
    string? OldChannel,
    string? NewChannel,
    string? OldRadioId,
    string? NewRadioId,
    WgbRoamClassification RoamClassification,
    string? OldRssi = null);

public enum RealtimeGraphMarkerKind
{
    LossStarted,
    Recovered,
    ParentApChanged
}
