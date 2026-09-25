using System.Globalization;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Wgb;

namespace WgbDiagnostics.Core.Realtime;

public enum LiveDiagnosticsLayout
{
    Auto,
    Horizontal,
    Vertical
}

public enum LiveIcmpDisplayMode
{
    AllPings,
    EventsOnly,
    LossWindow
}

public enum LiveWgbDisplayMode
{
    AllSamples,
    ChangesOnly,
    RoamsOnly
}

public enum LiveDiagnosticsPanel
{
    Icmp,
    Wgb
}

public enum LiveDiagnosticsSeverity
{
    Neutral,
    Success,
    Warning,
    Critical
}

public sealed record LiveDiagnosticsRow(
    long OrderId,
    DateTimeOffset Timestamp,
    LiveDiagnosticsPanel Panel,
    string EventName,
    LiveDiagnosticsSeverity Severity,
    string Text);

public sealed record LiveDiagnosticsSnapshot(
    IReadOnlyList<LiveDiagnosticsRow> IcmpRows,
    IReadOnlyList<LiveDiagnosticsRow> WgbRows,
    int IcmpSourceEventCount,
    int WgbSourceEventCount);

public sealed record LiveDiagnosticsApplyResult(
    bool Accepted,
    LiveDiagnosticsPanel Panel,
    int TrimmedCount);

public sealed class LiveDiagnosticsPanelViewport
{
    public bool IsPaused { get; private set; }

    public bool IsAutoscrollEnabled { get; private set; } = true;

    public int NewEventsWhileViewingOlderData { get; private set; }

    public bool IsViewingOlderData => IsPaused || !IsAutoscrollEnabled;

    public void Pause()
    {
        IsPaused = true;
    }

    public void Resume()
    {
        IsPaused = false;
    }

    public void UserScrolledAwayFromLatest()
    {
        IsAutoscrollEnabled = false;
    }

    public void UserReachedLatest()
    {
        if (IsPaused)
        {
            return;
        }

        IsAutoscrollEnabled = true;
        NewEventsWhileViewingOlderData = 0;
    }

    public void JumpToLatest()
    {
        IsPaused = false;
        IsAutoscrollEnabled = true;
        NewEventsWhileViewingOlderData = 0;
    }

    public void NoteEventsAdded(int count)
    {
        if (count <= 0 || !IsViewingOlderData)
        {
            return;
        }

        NewEventsWhileViewingOlderData += count;
    }
}

public sealed class LiveDiagnosticsPresentationModel
{
    public const int DefaultBufferSize = 10_000;
    public const int MinimumBufferSize = 1;
    public const int MaximumBufferSize = 100_000;

    private static readonly TimeSpan ParserFailureThrottleWindow = TimeSpan.FromSeconds(60);

    private readonly List<SequencedIcmpEvent> _icmpEvents = [];
    private readonly List<SequencedWgbEvent> _wgbEvents = [];
    private readonly HashSet<string> _icmpEventKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wgbEventKeys = new(StringComparer.Ordinal);
    private long _nextOrderId;

    public LiveDiagnosticsPresentationModel(int bufferSize = DefaultBufferSize)
    {
        BufferSize = NormalizeBufferSize(bufferSize);
    }

    public int BufferSize { get; private set; }

    public LiveIcmpDisplayMode IcmpDisplayMode { get; private set; } = LiveIcmpDisplayMode.AllPings;

    public LiveWgbDisplayMode WgbDisplayMode { get; private set; } = LiveWgbDisplayMode.AllSamples;

    public void Reset()
    {
        _icmpEvents.Clear();
        _wgbEvents.Clear();
        _icmpEventKeys.Clear();
        _wgbEventKeys.Clear();
        _nextOrderId = 0;
    }

    public void ConfigureBufferSize(int bufferSize)
    {
        BufferSize = NormalizeBufferSize(bufferSize);
        TrimIcmpEvents();
        TrimWgbEvents();
    }

    public void SetIcmpDisplayMode(LiveIcmpDisplayMode mode)
    {
        IcmpDisplayMode = mode;
    }

    public void SetWgbDisplayMode(LiveWgbDisplayMode mode)
    {
        WgbDisplayMode = mode;
    }

    public LiveDiagnosticsApplyResult Apply(IcmpMonitorEvent monitorEvent)
    {
        var key = CreateIcmpEventKey(monitorEvent);
        if (!_icmpEventKeys.Add(key))
        {
            return new LiveDiagnosticsApplyResult(false, LiveDiagnosticsPanel.Icmp, 0);
        }

        var sourceEvent = new SequencedIcmpEvent(++_nextOrderId, monitorEvent, key);
        InsertSorted(_icmpEvents, sourceEvent, SequencedIcmpEventComparer.Instance);
        var trimmed = TrimIcmpEvents();
        return new LiveDiagnosticsApplyResult(true, LiveDiagnosticsPanel.Icmp, trimmed);
    }

    public LiveDiagnosticsApplyResult Apply(WgbPollEvent pollEvent)
    {
        var key = CreateWgbEventKey(pollEvent);
        if (!_wgbEventKeys.Add(key))
        {
            return new LiveDiagnosticsApplyResult(false, LiveDiagnosticsPanel.Wgb, 0);
        }

        var sourceEvent = SequencedWgbEvent.FromPollEvent(++_nextOrderId, pollEvent, key);
        InsertSorted(_wgbEvents, sourceEvent, SequencedWgbEventComparer.Instance);
        var trimmed = TrimWgbEvents();
        return new LiveDiagnosticsApplyResult(true, LiveDiagnosticsPanel.Wgb, trimmed);
    }

    public LiveDiagnosticsApplyResult ApplyWgbStale(DateTimeOffset timestamp, WgbRealtimeStatus status)
    {
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"stale:{timestamp.UtcTicks}:{status.LastSuccessfulPollTimestamp?.UtcTicks}:{status.ParentApName}:{status.DataAge.TotalMilliseconds:0}");
        if (!_wgbEventKeys.Add(key))
        {
            return new LiveDiagnosticsApplyResult(false, LiveDiagnosticsPanel.Wgb, 0);
        }

        var message = $"STALE DATA age={FormatDuration(status.DataAge)} AP={FormatNullable(status.ParentApName)} BSSID={FormatNullable(status.ParentBssid)} RSSI={WgbAssociationSample.FormatRssi(status.Rssi)}";
        var sourceEvent = SequencedWgbEvent.FromSynthetic(
            ++_nextOrderId,
            timestamp,
            key,
            "STALE",
            LiveDiagnosticsSeverity.Warning,
            message);
        InsertSorted(_wgbEvents, sourceEvent, SequencedWgbEventComparer.Instance);
        var trimmed = TrimWgbEvents();
        return new LiveDiagnosticsApplyResult(true, LiveDiagnosticsPanel.Wgb, trimmed);
    }

    public LiveDiagnosticsApplyResult ApplyWgbRecovered(DateTimeOffset timestamp, WgbRealtimeStatus status)
    {
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"recovered:{timestamp.UtcTicks}:{status.LastSuccessfulPollTimestamp?.UtcTicks}:{status.ParentApName}");
        if (!_wgbEventKeys.Add(key))
        {
            return new LiveDiagnosticsApplyResult(false, LiveDiagnosticsPanel.Wgb, 0);
        }

        var message = $"RECOVERED AP={FormatNullable(status.ParentApName)} RSSI={WgbAssociationSample.FormatRssi(status.Rssi)}";
        var sourceEvent = SequencedWgbEvent.FromSynthetic(
            ++_nextOrderId,
            timestamp,
            key,
            "RECOVERED",
            LiveDiagnosticsSeverity.Success,
            message);
        InsertSorted(_wgbEvents, sourceEvent, SequencedWgbEventComparer.Instance);
        var trimmed = TrimWgbEvents();
        return new LiveDiagnosticsApplyResult(true, LiveDiagnosticsPanel.Wgb, trimmed);
    }

    public LiveDiagnosticsSnapshot Snapshot()
    {
        return new LiveDiagnosticsSnapshot(
            BuildIcmpRows(),
            BuildWgbRows(),
            _icmpEvents.Count,
            _wgbEvents.Count);
    }

    private IReadOnlyList<LiveDiagnosticsRow> BuildIcmpRows()
    {
        return IcmpDisplayMode switch
        {
            LiveIcmpDisplayMode.EventsOnly => BuildIcmpEventsOnlyRows(),
            LiveIcmpDisplayMode.LossWindow => BuildIcmpLossWindowRows(),
            _ => _icmpEvents.Select(sourceEvent => CreateIcmpRow(sourceEvent)).ToArray()
        };
    }

    private IReadOnlyList<LiveDiagnosticsRow> BuildIcmpEventsOnlyRows()
    {
        var rows = new List<LiveDiagnosticsRow>();
        SequencedIcmpEvent? lastOk = null;

        foreach (var sourceEvent in _icmpEvents)
        {
            var monitorEvent = sourceEvent.Event;
            if (monitorEvent.Kind == IcmpMonitorEventKind.PacketLoss)
            {
                rows.Add(CreateIcmpRow(sourceEvent));
                continue;
            }

            if (!monitorEvent.AppliedToState)
            {
                continue;
            }

            switch (monitorEvent.Kind)
            {
                case IcmpMonitorEventKind.PingReply:
                    lastOk = sourceEvent;
                    break;
                case IcmpMonitorEventKind.LossStarted:
                    if (lastOk is not null)
                    {
                        rows.Add(CreateIcmpRow(lastOk, eventNameOverride: "LAST_OK"));
                    }

                    rows.Add(CreateIcmpRow(sourceEvent, eventNameOverride: "LOSS_START"));
                    break;
                case IcmpMonitorEventKind.AlertThresholdReached:
                case IcmpMonitorEventKind.Recovered:
                case IcmpMonitorEventKind.Error:
                    rows.Add(CreateIcmpRow(sourceEvent));
                    break;
            }
        }

        return rows;
    }

    private IReadOnlyList<LiveDiagnosticsRow> BuildIcmpLossWindowRows()
    {
        var rows = new List<LiveDiagnosticsRow>();
        SequencedIcmpEvent? normalRunStart = null;
        SequencedIcmpEvent? normalRunEnd = null;
        var normalRunCount = 0;
        var insideLossWindow = false;

        foreach (var sourceEvent in _icmpEvents)
        {
            var monitorEvent = sourceEvent.Event;
            if (monitorEvent.Kind == IcmpMonitorEventKind.PacketLoss)
            {
                FlushNormalRun();
                rows.Add(CreateIcmpRow(sourceEvent));
                continue;
            }

            if (!monitorEvent.AppliedToState)
            {
                continue;
            }

            if (!insideLossWindow && monitorEvent.Kind == IcmpMonitorEventKind.PingReply)
            {
                normalRunStart ??= sourceEvent;
                normalRunEnd = sourceEvent;
                normalRunCount++;
                continue;
            }

            if (!insideLossWindow && IsLossWindowEvent(monitorEvent.Kind))
            {
                FlushNormalRun();
                insideLossWindow = true;
                rows.Add(CreateIcmpRow(sourceEvent));
                if (monitorEvent.Kind == IcmpMonitorEventKind.Recovered)
                {
                    insideLossWindow = false;
                }

                continue;
            }

            if (insideLossWindow)
            {
                rows.Add(CreateIcmpRow(sourceEvent));
                if (monitorEvent.Kind == IcmpMonitorEventKind.Recovered)
                {
                    insideLossWindow = false;
                }

                continue;
            }

            rows.Add(CreateIcmpRow(sourceEvent));
        }

        FlushNormalRun();
        return rows;

        void FlushNormalRun()
        {
            if (normalRunStart is null || normalRunEnd is null || normalRunCount == 0)
            {
                return;
            }

            var first = normalRunStart.Event;
            var last = normalRunEnd.Event;
            var text = normalRunCount == 1
                ? $"{FormatTime(first.Timestamp)}  OK_PERIOD  count=1 seq={first.SequenceNumber} rtt={FormatRoundTripTime(first.RoundTripTime)}"
                : $"{FormatTime(first.Timestamp)}-{FormatTime(last.Timestamp)}  OK_PERIOD  count={normalRunCount} seq={first.SequenceNumber}-{last.SequenceNumber} last_rtt={FormatRoundTripTime(last.RoundTripTime)}";
            rows.Add(new LiveDiagnosticsRow(
                normalRunStart.OrderId * 100,
                first.Timestamp,
                LiveDiagnosticsPanel.Icmp,
                "OK_PERIOD",
                LiveDiagnosticsSeverity.Neutral,
                text));
            normalRunStart = null;
            normalRunEnd = null;
            normalRunCount = 0;
        }
    }

    private IReadOnlyList<LiveDiagnosticsRow> BuildWgbRows()
    {
        var rows = new List<LiveDiagnosticsRow>();
        var parserFailures = new Dictionary<string, ParserFailureThrottleState>(StringComparer.OrdinalIgnoreCase);
        WgbAssociationSample? previousSample = null;

        foreach (var sourceEvent in _wgbEvents)
        {
            if (sourceEvent.PollEvent is null)
            {
                AddSyntheticWgbRow(rows, sourceEvent);
                continue;
            }

            var pollEvent = sourceEvent.PollEvent;
            if (WgbAssociationSample.TryCreate(pollEvent, out var sample) && sample is not null)
            {
                var transition = previousSample is not null && sample.HasRoamTransitionFrom(previousSample);
                var operatorChange = previousSample is null || sample.HasOperatorChangeFrom(previousSample);

                if (transition && previousSample is not null && WgbDisplayMode != LiveWgbDisplayMode.ChangesOnly)
                {
                    rows.Add(CreateWgbRoamRow(sourceEvent, previousSample, sample, subOrder: 0));
                }

                if (WgbDisplayMode == LiveWgbDisplayMode.AllSamples
                    || (WgbDisplayMode == LiveWgbDisplayMode.ChangesOnly && operatorChange))
                {
                    rows.Add(CreateWgbSampleRow(
                        sourceEvent,
                        sample,
                        transition && WgbDisplayMode == LiveWgbDisplayMode.AllSamples ? 1 : 0));
                }

                if (transition && previousSample is not null && WgbDisplayMode == LiveWgbDisplayMode.ChangesOnly)
                {
                    rows.Add(CreateWgbRoamRow(sourceEvent, previousSample, sample, subOrder: 1));
                }

                previousSample = sample;
                continue;
            }

            var stateRow = CreateWgbStateRow(sourceEvent, parserFailures);
            if (stateRow is not null)
            {
                rows.Add(stateRow);
            }
        }

        return rows;
    }

    private LiveDiagnosticsRow CreateIcmpRow(
        SequencedIcmpEvent sourceEvent,
        string? eventNameOverride = null)
    {
        var monitorEvent = sourceEvent.Event;
        var eventName = eventNameOverride ?? ToIcmpEventName(monitorEvent);
        var severity = ToIcmpSeverity(monitorEvent, eventName);
        var parts = new List<string>
        {
            $"{FormatTime(monitorEvent.Timestamp)}  {eventName,-10}",
            $"seq={monitorEvent.SequenceNumber}"
        };

        if (monitorEvent.RoundTripTime is not null)
        {
            parts.Add(FormatRoundTripTime(monitorEvent.RoundTripTime));
        }

        if (monitorEvent.Kind == IcmpMonitorEventKind.PacketLoss)
        {
            parts.Add("late_timeout");
            parts.Add("state_unchanged");
            return new LiveDiagnosticsRow(
                sourceEvent.OrderId * 100,
                monitorEvent.Timestamp,
                LiveDiagnosticsPanel.Icmp,
                eventName,
                severity,
                string.Join("  ", parts));
        }

        if (!monitorEvent.AppliedToState)
        {
            parts.Add("ignored_for_state");
            if (!string.IsNullOrWhiteSpace(monitorEvent.IgnoredReason))
            {
                parts.Add($"reason={monitorEvent.IgnoredReason}");
            }

            return new LiveDiagnosticsRow(
                sourceEvent.OrderId * 100,
                monitorEvent.Timestamp,
                LiveDiagnosticsPanel.Icmp,
                eventName,
                severity,
                string.Join("  ", parts));
        }

        if (monitorEvent.ConsecutiveLoss > 0)
        {
            parts.Add($"loss={monitorEvent.ConsecutiveLoss}");
        }

        if (monitorEvent.EstimatedLossWindowMilliseconds > 0)
        {
            var label = monitorEvent.Kind == IcmpMonitorEventKind.Recovered ? "outage" : "window";
            parts.Add($"{label}={FormatDuration(TimeSpan.FromMilliseconds(monitorEvent.EstimatedLossWindowMilliseconds))}");
        }

        if (!string.IsNullOrWhiteSpace(monitorEvent.Message)
            && monitorEvent.Kind is IcmpMonitorEventKind.Error or IcmpMonitorEventKind.Loss)
        {
            parts.Add(monitorEvent.Message.Trim());
        }

        return new LiveDiagnosticsRow(
            sourceEvent.OrderId * 100,
            monitorEvent.Timestamp,
            LiveDiagnosticsPanel.Icmp,
            eventName,
            severity,
            string.Join("  ", parts));
    }

    private void AddSyntheticWgbRow(
        ICollection<LiveDiagnosticsRow> rows,
        SequencedWgbEvent sourceEvent)
    {
        if (WgbDisplayMode == LiveWgbDisplayMode.RoamsOnly)
        {
            return;
        }

        rows.Add(new LiveDiagnosticsRow(
            sourceEvent.OrderId * 100,
            sourceEvent.Timestamp,
            LiveDiagnosticsPanel.Wgb,
            sourceEvent.SyntheticEventName ?? "WGB",
            sourceEvent.SyntheticSeverity,
            $"{FormatTime(sourceEvent.Timestamp)} {sourceEvent.SyntheticMessage}"));
    }

    private static LiveDiagnosticsRow CreateWgbSampleRow(
        SequencedWgbEvent sourceEvent,
        WgbAssociationSample sample,
        int subOrder)
    {
        return new LiveDiagnosticsRow(
            sourceEvent.OrderId * 100 + subOrder,
            sample.Timestamp,
            LiveDiagnosticsPanel.Wgb,
            "SAMPLE",
            LiveDiagnosticsSeverity.Success,
            sample.FormatOperatorRow());
    }

    private LiveDiagnosticsRow CreateWgbRoamRow(
        SequencedWgbEvent sourceEvent,
        WgbAssociationSample previous,
        WgbAssociationSample current,
        int subOrder)
    {
        var classification = WgbRoamClassifier.Classify(previous.ToAssociationSnapshot(), current.ToAssociationSnapshot());
        return new LiveDiagnosticsRow(
            sourceEvent.OrderId * 100 + subOrder,
            current.Timestamp,
            LiveDiagnosticsPanel.Wgb,
            "ROAM",
            LiveDiagnosticsSeverity.Warning,
            current.FormatRoamRow(previous, classification));
    }

    private LiveDiagnosticsRow? CreateWgbStateRow(
        SequencedWgbEvent sourceEvent,
        IDictionary<string, ParserFailureThrottleState> parserFailures)
    {
        var pollEvent = sourceEvent.PollEvent;
        if (pollEvent is null)
        {
            return null;
        }

        var eventName = pollEvent.Kind switch
        {
            WgbPollEventKind.PollFailed => IsParserFailure(pollEvent) ? "PARSER_FAILURE" : "POLL_FAILED",
            WgbPollEventKind.Disconnected or WgbPollEventKind.PromptResyncFailed => "DISCONNECTED",
            WgbPollEventKind.ReconnectScheduled => "RECONNECTING",
            WgbPollEventKind.Connected => "RECONNECTED",
            _ => null
        };
        if (eventName is null)
        {
            return null;
        }

        if (WgbDisplayMode == LiveWgbDisplayMode.RoamsOnly && eventName is not ("DISCONNECTED" or "RECONNECTING" or "RECONNECTED"))
        {
            return null;
        }

        var message = pollEvent.Message?.Trim();
        var suffix = "";
        if (eventName == "PARSER_FAILURE")
        {
            var key = string.IsNullOrWhiteSpace(message) ? eventName : message;
            if (parserFailures.TryGetValue(key, out var state))
            {
                if (pollEvent.Timestamp - state.LastShownAt < ParserFailureThrottleWindow)
                {
                    state.SuppressedCount++;
                    return null;
                }

                if (state.SuppressedCount > 0)
                {
                    suffix = $" parser warning repeated {state.SuppressedCount} times";
                    state.SuppressedCount = 0;
                }

                state.LastShownAt = pollEvent.Timestamp;
            }
            else
            {
                parserFailures[key] = new ParserFailureThrottleState(pollEvent.Timestamp);
            }
        }

        var severity = eventName switch
        {
            "RECONNECTED" => LiveDiagnosticsSeverity.Success,
            "RECONNECTING" => LiveDiagnosticsSeverity.Warning,
            _ => LiveDiagnosticsSeverity.Critical
        };
        var reason = string.IsNullOrWhiteSpace(message) ? "" : $" reason=\"{EscapeReason(message)}\"";
        var text = $"{FormatTime(pollEvent.Timestamp)} {eventName}{reason}{suffix}";
        return new LiveDiagnosticsRow(
            sourceEvent.OrderId * 100,
            pollEvent.Timestamp,
            LiveDiagnosticsPanel.Wgb,
            eventName,
            severity,
            text);
    }

    private LiveDiagnosticsRow CreateWgbRow(SequencedWgbEvent sourceEvent, int subOrder)
    {
        if (sourceEvent.PollEvent is null)
        {
            return new LiveDiagnosticsRow(
                sourceEvent.OrderId * 100 + subOrder,
                sourceEvent.Timestamp,
                LiveDiagnosticsPanel.Wgb,
                sourceEvent.SyntheticEventName ?? "WGB",
                sourceEvent.SyntheticSeverity,
                $"{FormatTime(sourceEvent.Timestamp)}  {sourceEvent.SyntheticMessage}");
        }

        var pollEvent = sourceEvent.PollEvent;
        var eventName = ToWgbEventName(pollEvent);
        var severity = ToWgbSeverity(pollEvent, eventName);
        var text = eventName == "ROAM"
            ? FormatRoamEvent(sourceEvent, pollEvent, eventName)
            : FormatCompactWgbEvent(pollEvent, eventName);

        return new LiveDiagnosticsRow(
            sourceEvent.OrderId * 100 + subOrder,
            pollEvent.Timestamp,
            LiveDiagnosticsPanel.Wgb,
            eventName,
            severity,
            text);
    }

    private LiveDiagnosticsRow CreateParserWarningRow(
        SequencedWgbEvent sourceEvent,
        WgbAssociationParseResult parseResult)
    {
        var pollEvent = sourceEvent.PollEvent!;
        var text = $"{FormatTime(pollEvent.Timestamp)}  PARSER WARNING  profile={parseResult.ParserProfile} missing={parseResult.MissingFields.Count} unclassified={parseResult.UnclassifiedLines.Count}";
        return new LiveDiagnosticsRow(
            sourceEvent.OrderId * 100 + 1,
            pollEvent.Timestamp,
            LiveDiagnosticsPanel.Wgb,
            "PARSER WARNING",
            LiveDiagnosticsSeverity.Warning,
            text);
    }

    private static string FormatRoamEvent(
        SequencedWgbEvent sourceEvent,
        WgbPollEvent pollEvent,
        string eventName)
    {
        var oldRssi = pollEvent.OldRssi;
        var newRssi = pollEvent.NewRssi ?? pollEvent.Association?.Rssi;
        return string.Join(
            Environment.NewLine,
            $"{FormatTime(pollEvent.Timestamp)}  {eventName}",
            $"{FormatNullable(pollEvent.OldParentApName)} -> {FormatNullable(pollEvent.NewParentApName)}",
            $"BSSID {FormatNullable(pollEvent.OldParentBssid)} -> {FormatNullable(pollEvent.NewParentBssid)}",
            $"Channel {FormatNullable(pollEvent.OldChannel)} -> {FormatNullable(pollEvent.NewChannel)}",
            $"Radio {FormatNullable(pollEvent.OldRadioId)} -> {FormatNullable(pollEvent.NewRadioId)}",
            $"RSSI {WgbAssociationSample.FormatRssi(oldRssi)} -> {WgbAssociationSample.FormatRssi(newRssi)}",
            $"Classification: {pollEvent.RoamClassification}");
    }

    private static string FormatCompactWgbEvent(WgbPollEvent pollEvent, string eventName)
    {
        var parts = new List<string>
        {
            $"{FormatTime(pollEvent.Timestamp)}  {eventName}"
        };

        if (pollEvent.Association is not null)
        {
            parts.Add($"AP={FormatNullable(pollEvent.Association.ParentApName)}");
            parts.Add($"BSSID={FormatNullable(pollEvent.Association.ParentBssid)}");
            parts.Add($"RSSI={WgbAssociationSample.FormatRssi(pollEvent.Association.Rssi)}");
            parts.Add($"CH={FormatNullable(pollEvent.Association.Channel)}");
            parts.Add($"R={FormatNullable(pollEvent.Association.RadioId)}");
            parts.Add($"Tx={FormatNullable(pollEvent.Association.TxRate)}");
            parts.Add($"Rx={FormatNullable(pollEvent.Association.RxRate)}");
            parts.Add($"Assoc={FormatNullable(pollEvent.Association.AssociationStatus)}");
        }

        if (!string.IsNullOrWhiteSpace(pollEvent.Message))
        {
            parts.Add(pollEvent.Message.Trim());
        }

        return string.Join("  ", parts);
    }

    private int TrimIcmpEvents()
    {
        var trimmed = TrimOldest(_icmpEvents, _icmpEventKeys);
        return trimmed;
    }

    private int TrimWgbEvents()
    {
        var trimmed = TrimOldest(_wgbEvents, _wgbEventKeys);
        return trimmed;
    }

    private int TrimOldest<T>(List<T> events, HashSet<string> eventKeys)
        where T : ISequencedEvent
    {
        if (events.Count <= BufferSize)
        {
            return 0;
        }

        var removeCount = events.Count - BufferSize;
        for (var i = 0; i < removeCount; i++)
        {
            eventKeys.Remove(events[i].EventKey);
        }

        events.RemoveRange(0, removeCount);
        return removeCount;
    }

    private static void InsertSorted<T>(
        List<T> items,
        T item,
        IComparer<T> comparer)
    {
        var index = items.BinarySearch(item, comparer);
        if (index < 0)
        {
            index = ~index;
        }

        items.Insert(index, item);
    }

    private static string CreateIcmpEventKey(IcmpMonitorEvent monitorEvent)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{monitorEvent.Timestamp.UtcTicks}:{monitorEvent.SequenceNumber}:{monitorEvent.Kind}:{monitorEvent.RoundTripTime?.Ticks}:{monitorEvent.ConsecutiveLoss}:{monitorEvent.EstimatedLossWindowMilliseconds}:{monitorEvent.Message}:{monitorEvent.StartedAtMilliseconds}:{monitorEvent.CompletedAtMilliseconds}:{monitorEvent.CompletionOrder}:{monitorEvent.AppliedToState}:{monitorEvent.IgnoredReason}:{monitorEvent.HighestSequenceAppliedToState}:{monitorEvent.HighestCompletedSequence}:{monitorEvent.ConnectionState}");
    }

    private static string CreateWgbEventKey(WgbPollEvent pollEvent)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{pollEvent.Timestamp.UtcTicks}:{pollEvent.Kind}:{pollEvent.Message}:{pollEvent.Association?.ParentApName}:{pollEvent.Association?.ParentBssid}:{pollEvent.Association?.Channel}:{pollEvent.Association?.Rssi}:{pollEvent.Association?.RadioId}:{pollEvent.Association?.TxRate}:{pollEvent.Association?.RxRate}:{pollEvent.Association?.AssociationStatus}:{pollEvent.OldParentApName}:{pollEvent.NewParentApName}:{pollEvent.OldParentBssid}:{pollEvent.NewParentBssid}:{pollEvent.OldChannel}:{pollEvent.NewChannel}:{pollEvent.OldRadioId}:{pollEvent.NewRadioId}:{pollEvent.RoamClassification}");
    }

    private static int NormalizeBufferSize(int bufferSize)
    {
        return Math.Clamp(bufferSize, MinimumBufferSize, MaximumBufferSize);
    }

    private static bool IsLossWindowEvent(IcmpMonitorEventKind kind)
    {
        return kind is IcmpMonitorEventKind.LossStarted
            or IcmpMonitorEventKind.PacketLoss
            or IcmpMonitorEventKind.Loss
            or IcmpMonitorEventKind.AlertThresholdReached
            or IcmpMonitorEventKind.Recovered
            or IcmpMonitorEventKind.Error;
    }

    private static string ToIcmpEventName(IcmpMonitorEventKind kind)
    {
        return kind switch
        {
            IcmpMonitorEventKind.PingReply => "OK",
            IcmpMonitorEventKind.PacketLoss => "PACKET_LOSS",
            IcmpMonitorEventKind.LossStarted => "LOSS_START",
            IcmpMonitorEventKind.Loss => "TIMEOUT",
            IcmpMonitorEventKind.AlertThresholdReached => "ALERT",
            IcmpMonitorEventKind.Recovered => "RECOVER",
            IcmpMonitorEventKind.Error => "ERROR",
            _ => kind.ToString().ToUpperInvariant()
        };
    }

    private static string ToIcmpEventName(IcmpMonitorEvent monitorEvent)
    {
        if (monitorEvent.Kind == IcmpMonitorEventKind.PacketLoss)
        {
            return "PACKET_LOSS";
        }

        if (monitorEvent.AppliedToState)
        {
            return ToIcmpEventName(monitorEvent.Kind);
        }

        return monitorEvent.Kind switch
        {
            IcmpMonitorEventKind.PingReply => "LATE_OK",
            IcmpMonitorEventKind.Error => "LATE_ERROR",
            _ => "LATE_TIMEOUT"
        };
    }

    private static LiveDiagnosticsSeverity ToIcmpSeverity(
        IcmpMonitorEvent monitorEvent,
        string eventName)
    {
        if (eventName == "LAST_OK")
        {
            return LiveDiagnosticsSeverity.Success;
        }

        if (!monitorEvent.AppliedToState)
        {
            return eventName == "LATE_OK"
                ? LiveDiagnosticsSeverity.Neutral
                : LiveDiagnosticsSeverity.Warning;
        }

        return monitorEvent.Kind switch
        {
            IcmpMonitorEventKind.PingReply or IcmpMonitorEventKind.Recovered => LiveDiagnosticsSeverity.Success,
            IcmpMonitorEventKind.PacketLoss => LiveDiagnosticsSeverity.Warning,
            IcmpMonitorEventKind.LossStarted or IcmpMonitorEventKind.Loss => LiveDiagnosticsSeverity.Warning,
            IcmpMonitorEventKind.AlertThresholdReached or IcmpMonitorEventKind.Error => LiveDiagnosticsSeverity.Critical,
            _ => LiveDiagnosticsSeverity.Neutral
        };
    }

    private static string ToWgbEventName(WgbPollEvent pollEvent)
    {
        if (pollEvent.Kind == WgbPollEventKind.PollFailed
            && pollEvent.Message?.Contains("parser", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "PARSER FAILURE";
        }

        return pollEvent.Kind switch
        {
            WgbPollEventKind.PollSucceeded => "POLL SUCCESS",
            WgbPollEventKind.PollFailed => "POLL FAILURE",
            WgbPollEventKind.SessionLost or WgbPollEventKind.Disconnected => "SSH DISCONNECTED",
            WgbPollEventKind.Connecting or WgbPollEventKind.ReconnectScheduled => "SSH RECONNECTING",
            WgbPollEventKind.Connected => "SSH RECONNECTED",
            WgbPollEventKind.AssociationUpdated => "ASSOCIATION UPDATE",
            WgbPollEventKind.ParentApChanged => "ROAM",
            WgbPollEventKind.CommandWarning => "PARSER WARNING",
            WgbPollEventKind.PromptResyncFailed => "SSH DISCONNECTED",
            _ => pollEvent.Kind.ToString().ToUpperInvariant()
        };
    }

    private static LiveDiagnosticsSeverity ToWgbSeverity(
        WgbPollEvent pollEvent,
        string eventName)
    {
        if (eventName is "PARSER FAILURE" or "POLL FAILURE" or "SSH DISCONNECTED")
        {
            return LiveDiagnosticsSeverity.Critical;
        }

        if (eventName is "ROAM" or "PARSER WARNING" or "STALE DATA" or "SSH RECONNECTING")
        {
            return LiveDiagnosticsSeverity.Warning;
        }

        return pollEvent.Kind is WgbPollEventKind.PollSucceeded or WgbPollEventKind.Connected
            ? LiveDiagnosticsSeverity.Success
            : LiveDiagnosticsSeverity.Neutral;
    }

    private static bool IsParserFailure(WgbPollEvent pollEvent)
    {
        return pollEvent.Kind == WgbPollEventKind.PollFailed
            && pollEvent.Message?.Contains("parser", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string EscapeReason(string value)
    {
        return value.Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string FormatTime(DateTimeOffset timestamp)
    {
        return WgbAssociationSample.FormatTimestamp(timestamp);
    }

    private static string FormatRoundTripTime(TimeSpan? roundTripTime)
    {
        return roundTripTime is null
            ? "-"
            : $"{roundTripTime.Value.TotalMilliseconds:0} ms";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMilliseconds < 1000)
        {
            return $"{duration.TotalMilliseconds:0} ms";
        }

        return duration.TotalMinutes < 1
            ? $"{duration.TotalSeconds:0.0} s"
            : duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private interface ISequencedEvent
    {
        long OrderId { get; }

        DateTimeOffset Timestamp { get; }

        string EventKey { get; }
    }

    private sealed record SequencedIcmpEvent(
        long OrderId,
        IcmpMonitorEvent Event,
        string EventKey) : ISequencedEvent
    {
        public DateTimeOffset Timestamp => Event.Timestamp;
    }

    private sealed record SequencedWgbEvent(
        long OrderId,
        DateTimeOffset Timestamp,
        string EventKey,
        WgbPollEvent? PollEvent,
        string? SyntheticEventName,
        LiveDiagnosticsSeverity SyntheticSeverity,
        string? SyntheticMessage) : ISequencedEvent
    {
        public static SequencedWgbEvent FromPollEvent(
            long orderId,
            WgbPollEvent pollEvent,
            string eventKey)
        {
            return new SequencedWgbEvent(
                orderId,
                pollEvent.Timestamp,
                eventKey,
                pollEvent,
                SyntheticEventName: null,
                LiveDiagnosticsSeverity.Neutral,
                SyntheticMessage: null);
        }

        public static SequencedWgbEvent FromSynthetic(
            long orderId,
            DateTimeOffset timestamp,
            string eventKey,
            string eventName,
            LiveDiagnosticsSeverity severity,
            string message)
        {
            return new SequencedWgbEvent(
                orderId,
                timestamp,
                eventKey,
                PollEvent: null,
                eventName,
                severity,
                message);
        }
    }

    private sealed class SequencedIcmpEventComparer : IComparer<SequencedIcmpEvent>
    {
        public static SequencedIcmpEventComparer Instance { get; } = new();

        public int Compare(SequencedIcmpEvent? x, SequencedIcmpEvent? y)
        {
            var timestampComparison = Nullable.Compare(x?.Timestamp, y?.Timestamp);
            return timestampComparison != 0
                ? timestampComparison
                : Nullable.Compare(x?.OrderId, y?.OrderId);
        }
    }

    private sealed class SequencedWgbEventComparer : IComparer<SequencedWgbEvent>
    {
        public static SequencedWgbEventComparer Instance { get; } = new();

        public int Compare(SequencedWgbEvent? x, SequencedWgbEvent? y)
        {
            var timestampComparison = Nullable.Compare(x?.Timestamp, y?.Timestamp);
            return timestampComparison != 0
                ? timestampComparison
                : Nullable.Compare(x?.OrderId, y?.OrderId);
        }
    }

    private sealed class ParserFailureThrottleState
    {
        public ParserFailureThrottleState(DateTimeOffset lastShownAt)
        {
            LastShownAt = lastShownAt;
        }

        public DateTimeOffset LastShownAt { get; set; }

        public int SuppressedCount { get; set; }
    }
}
