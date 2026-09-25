namespace WgbDiagnostics.Core.Monitoring;

public sealed record IcmpMonitorEvent(
    IcmpMonitorEventKind Kind,
    DateTimeOffset Timestamp,
    long SequenceNumber,
    TimeSpan? RoundTripTime,
    int ConsecutiveLoss,
    int EstimatedLossWindowMilliseconds,
    string? Message,
    long StartedAtMilliseconds = 0,
    long CompletedAtMilliseconds = 0,
    long CompletionOrder = 0,
    bool AppliedToState = true,
    string? IgnoredReason = null,
    long HighestSequenceAppliedToState = 0,
    long HighestCompletedSequence = 0,
    string ConnectionState = "Unknown");
