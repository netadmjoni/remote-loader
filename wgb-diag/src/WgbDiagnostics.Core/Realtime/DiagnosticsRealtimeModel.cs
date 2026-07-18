using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Wgb;

namespace WgbDiagnostics.Core.Realtime;

public sealed class DiagnosticsRealtimeModel
{
    private readonly object _sync = new();
    private readonly List<PingObservation> _pingObservations = [];
    private readonly List<RealtimeGraphMarker> _markers = [];
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
            _markers.Clear();
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _pingObservations.Clear();
            _markers.Clear();
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
            ApplyWgbStatus(pollEvent);

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
                _markers.ToArray(),
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
                Status = "OK"
            },
            IcmpMonitorEventKind.LossStarted => status with
            {
                CurrentRoundTripTime = null,
                TotalLost = status.TotalLost + 1,
                ConsecutiveLoss = monitorEvent.ConsecutiveLoss,
                Status = "Loss"
            },
            IcmpMonitorEventKind.Loss => status with
            {
                CurrentRoundTripTime = null,
                TotalLost = status.TotalLost + 1,
                ConsecutiveLoss = monitorEvent.ConsecutiveLoss,
                Status = "Loss"
            },
            IcmpMonitorEventKind.AlertThresholdReached => status with
            {
                CurrentRoundTripTime = null,
                ConsecutiveLoss = monitorEvent.ConsecutiveLoss,
                Status = "Alert"
            },
            IcmpMonitorEventKind.Recovered => status with
            {
                CurrentRoundTripTime = monitorEvent.RoundTripTime,
                ConsecutiveLoss = 0,
                Status = "Recovered"
            },
            IcmpMonitorEventKind.Error => status with
            {
                CurrentRoundTripTime = null,
                ConsecutiveLoss = monitorEvent.ConsecutiveLoss,
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

    private void InsertMarker(RealtimeGraphMarker marker)
    {
        var index = _markers.BinarySearch(marker, RealtimeGraphMarkerTimestampComparer.Instance);
        if (index < 0)
        {
            index = ~index;
        }

        _markers.Insert(index, marker);
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

    private void TrimToWindow(DateTimeOffset anchor)
    {
        var cutoff = anchor - _options.VisibleWindow;
        _pingObservations.RemoveAll(observation => observation.Timestamp < cutoff);
        _markers.RemoveAll(marker => marker.Timestamp < cutoff);

        TrimOldest(_pingObservations, _options.MaxDataPoints);
        TrimOldest(_markers, _options.MaxMarkers);
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

    private sealed class PingObservationTimestampComparer : IComparer<PingObservation>
    {
        public static PingObservationTimestampComparer Instance { get; } = new();

        public int Compare(PingObservation? x, PingObservation? y)
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
    IReadOnlyList<RealtimeGraphMarker> Markers,
    PingRealtimeStatus PingStatus,
    WgbRealtimeStatus WgbStatus,
    RealtimeGraphOptions Options);

public sealed record RttGraphSegment(IReadOnlyList<RttGraphPoint> Points);

public sealed record RttGraphPoint(DateTimeOffset Timestamp, double RoundTripTimeMilliseconds);

public sealed record PingRealtimeStatus(
    TimeSpan? CurrentRoundTripTime,
    long TotalOk,
    long TotalLost,
    int ConsecutiveLoss,
    TimeSpan LongestOutage,
    TimeSpan Runtime,
    string Status)
{
    public static PingRealtimeStatus Empty { get; } = new(
        CurrentRoundTripTime: null,
        TotalOk: 0,
        TotalLost: 0,
        ConsecutiveLoss: 0,
        LongestOutage: TimeSpan.Zero,
        Runtime: TimeSpan.Zero,
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

public enum RealtimeGraphMarkerKind
{
    LossStarted,
    Recovered,
    ParentApChanged
}
