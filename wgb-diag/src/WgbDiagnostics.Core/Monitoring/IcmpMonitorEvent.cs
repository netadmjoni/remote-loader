namespace WgbDiagnostics.Core.Monitoring;

public sealed record IcmpMonitorEvent(
    IcmpMonitorEventKind Kind,
    DateTimeOffset Timestamp,
    long SequenceNumber,
    TimeSpan? RoundTripTime,
    int ConsecutiveLoss,
    int EstimatedLossWindowMilliseconds,
    string? Message);
