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
                break;

            case IcmpMonitorEventKind.PacketLoss:
                rows.Add(CreateOperatorEventRow(
                    monitorEvent,
                    PingEventSeverity.Warning,
                    "PACKET_LOSS",
                    $"Packet loss  {FormatProbeCount(1)}{FormatLossDuration(monitorEvent.EstimatedLossWindowMilliseconds)}"));
                break;

            case IcmpMonitorEventKind.LossStarted:
                if (!monitorEvent.AppliedToState)
                {
                    break;
                }

                rows.Add(CreateOperatorEventRow(
                    monitorEvent,
                    PingEventSeverity.Warning,
                    "LOSS_START",
                    $"Packet loss  {FormatProbeCount(Math.Max(1, monitorEvent.ConsecutiveLoss))}{FormatLossDuration(monitorEvent.EstimatedLossWindowMilliseconds)}"));
                break;

            case IcmpMonitorEventKind.AlertThresholdReached:
                if (!monitorEvent.AppliedToState)
                {
                    break;
                }

                rows.Add(CreateOperatorEventRow(
                    monitorEvent,
                    PingEventSeverity.Critical,
                    "ALERT",
                    $"Interruption  {FormatProbeCount(Math.Max(1, monitorEvent.ConsecutiveLoss))}{FormatLossDuration(monitorEvent.EstimatedLossWindowMilliseconds)}"));
                break;

            case IcmpMonitorEventKind.Recovered:
                if (!monitorEvent.AppliedToState)
                {
                    break;
                }

                rows.Add(CreateOperatorEventRow(
                    monitorEvent,
                    PingEventSeverity.Success,
                    "RECOVER",
                    $"Connectivity restored  outage {FormatDuration(monitorEvent.EstimatedLossWindowMilliseconds)}"));
                break;

            case IcmpMonitorEventKind.Error:
                if (!monitorEvent.AppliedToState)
                {
                    break;
                }

                rows.Add(CreateOperatorEventRow(
                    monitorEvent,
                    PingEventSeverity.Critical,
                    "ERROR",
                    $"Monitoring error{(string.IsNullOrWhiteSpace(monitorEvent.Message) ? "" : $"  {monitorEvent.Message.Trim()}")}"));
                break;
        }

        return mode == PingEventViewMode.Raw
            ? rows.Take(1).ToArray()
            : rows;
    }

    public void Reset()
    {
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

    private static PingEventRow CreateOperatorEventRow(
        IcmpMonitorEvent monitorEvent,
        PingEventSeverity severity,
        string eventName,
        string detail)
    {
        var timestamp = monitorEvent.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return new PingEventRow(
            monitorEvent.Timestamp,
            severity,
            eventName,
            $"{timestamp}  {detail}");
    }

    private static string FormatProbeCount(int count)
    {
        return $"{count} {(count == 1 ? "probe" : "probes")}";
    }

    private static string FormatLossDuration(int milliseconds)
    {
        return milliseconds > 0 ? $"  {FormatDuration(milliseconds)}" : "";
    }

    private static string FormatDuration(int milliseconds)
    {
        return milliseconds < 1000
            ? $"{milliseconds} ms"
            : $"{milliseconds / 1000d:0.0} s";
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
