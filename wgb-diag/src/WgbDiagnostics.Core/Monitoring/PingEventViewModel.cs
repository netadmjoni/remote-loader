using System.Globalization;

namespace WgbDiagnostics.Core.Monitoring;

public enum PingEventViewMode
{
    Event,
    Raw
}

public enum PingEventSeverity
{
    Neutral,
    Success,
    Warning,
    Critical
}

public sealed record PingEventRow(
    DateTimeOffset Timestamp,
    PingEventSeverity Severity,
    string EventName,
    string Text);

public sealed class PingEventViewModel
{
    private IcmpMonitorEvent? _lastOk;

    public IReadOnlyList<PingEventRow> Apply(
        IcmpMonitorEvent monitorEvent,
        PingEventViewMode mode)
    {
        var rows = new List<PingEventRow>();

        if (mode == PingEventViewMode.Raw)
        {
            rows.Add(CreateRawRow(monitorEvent));
        }

        switch (monitorEvent.Kind)
        {
            case IcmpMonitorEventKind.PingReply:
                if (monitorEvent.AppliedToState)
                {
                    _lastOk = monitorEvent;
                }

                break;

            case IcmpMonitorEventKind.PacketLoss:
                rows.Add(CreateEventRow(
                    monitorEvent,
                    PingEventSeverity.Warning,
                    "PACKET_LOSS",
                    $"PACKET_LOSS #{monitorEvent.SequenceNumber} late_timeout state_unchanged"));
                break;

            case IcmpMonitorEventKind.LossStarted:
                if (!monitorEvent.AppliedToState)
                {
                    break;
                }

                if (_lastOk is not null && mode == PingEventViewMode.Event)
                {
                    rows.Add(CreateLastOkRow(_lastOk));
                }

                rows.Add(CreateEventRow(
                    monitorEvent,
                    PingEventSeverity.Warning,
                    "LOSS_START",
                    $"LOSS_START #{monitorEvent.SequenceNumber} window={monitorEvent.EstimatedLossWindowMilliseconds} ms"));
                break;

            case IcmpMonitorEventKind.AlertThresholdReached:
                if (!monitorEvent.AppliedToState)
                {
                    break;
                }

                rows.Add(CreateEventRow(
                    monitorEvent,
                    PingEventSeverity.Critical,
                    "ALERT",
                    $"ALERT #{monitorEvent.SequenceNumber} outage={monitorEvent.EstimatedLossWindowMilliseconds} ms"));
                break;

            case IcmpMonitorEventKind.Recovered:
                if (!monitorEvent.AppliedToState)
                {
                    break;
                }

                rows.Add(CreateEventRow(
                    monitorEvent,
                    PingEventSeverity.Success,
                    "RECOVER",
                    $"RECOVER #{monitorEvent.SequenceNumber} outage={monitorEvent.EstimatedLossWindowMilliseconds} ms rtt={FormatRoundTripTime(monitorEvent.RoundTripTime)}"));
                break;

            case IcmpMonitorEventKind.Error:
                if (!monitorEvent.AppliedToState)
                {
                    break;
                }

                rows.Add(CreateEventRow(
                    monitorEvent,
                    PingEventSeverity.Critical,
                    "ERROR",
                    $"ERROR #{monitorEvent.SequenceNumber} {monitorEvent.Message}"));
                break;
        }

        return mode == PingEventViewMode.Raw
            ? rows.Take(1).ToArray()
            : rows;
    }

    public void Reset()
    {
        _lastOk = null;
    }

    private static PingEventRow CreateLastOkRow(IcmpMonitorEvent monitorEvent)
    {
        return CreateEventRow(
            monitorEvent,
            PingEventSeverity.Success,
            "LAST_OK",
            $"LAST_OK #{monitorEvent.SequenceNumber} rtt={FormatRoundTripTime(monitorEvent.RoundTripTime)}");
    }

    private static PingEventRow CreateRawRow(IcmpMonitorEvent monitorEvent)
    {
        var message = string.IsNullOrWhiteSpace(monitorEvent.Message)
            ? ""
            : $" {monitorEvent.Message}";
        var ignored = monitorEvent.AppliedToState
            ? ""
            : $" ignored_for_state reason={monitorEvent.IgnoredReason ?? "OutOfOrder"}";
        return CreateEventRow(
            monitorEvent,
            ToRawSeverity(monitorEvent),
            ToRawEventName(monitorEvent),
            $"{ToRawEventName(monitorEvent)} #{monitorEvent.SequenceNumber} rtt={FormatRoundTripTime(monitorEvent.RoundTripTime)} loss={monitorEvent.ConsecutiveLoss} window={monitorEvent.EstimatedLossWindowMilliseconds} ms{ignored}{message}");
    }

    private static PingEventRow CreateEventRow(
        IcmpMonitorEvent monitorEvent,
        PingEventSeverity severity,
        string eventName,
        string detail)
    {
        var timestamp = monitorEvent.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return new PingEventRow(
            monitorEvent.Timestamp,
            severity,
            eventName,
            $"{timestamp} {detail}");
    }

    private static string FormatRoundTripTime(TimeSpan? roundTripTime)
    {
        return roundTripTime is null
            ? "-"
            : $"{roundTripTime.Value.TotalMilliseconds:0} ms";
    }

    private static string ToRawEventName(IcmpMonitorEvent monitorEvent)
    {
        if (monitorEvent.Kind == IcmpMonitorEventKind.PacketLoss)
        {
            return monitorEvent.AppliedToState ? "PACKET_LOSS" : "LATE_TIMEOUT";
        }

        if (monitorEvent.AppliedToState)
        {
            return monitorEvent.Kind.ToString();
        }

        return monitorEvent.Kind switch
        {
            IcmpMonitorEventKind.PingReply => "LATE_OK",
            IcmpMonitorEventKind.Error => "LATE_ERROR",
            _ => "LATE_TIMEOUT"
        };
    }

    private static PingEventSeverity ToRawSeverity(IcmpMonitorEvent monitorEvent)
    {
        if (!monitorEvent.AppliedToState)
        {
            return monitorEvent.Kind == IcmpMonitorEventKind.PingReply
                ? PingEventSeverity.Neutral
                : PingEventSeverity.Warning;
        }

        return monitorEvent.Kind switch
        {
            IcmpMonitorEventKind.PingReply => PingEventSeverity.Neutral,
            IcmpMonitorEventKind.PacketLoss => PingEventSeverity.Warning,
            IcmpMonitorEventKind.Recovered => PingEventSeverity.Success,
            IcmpMonitorEventKind.LossStarted or IcmpMonitorEventKind.Loss => PingEventSeverity.Warning,
            IcmpMonitorEventKind.AlertThresholdReached or IcmpMonitorEventKind.Error => PingEventSeverity.Critical,
            _ => PingEventSeverity.Neutral
        };
    }
}
