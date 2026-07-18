namespace WgbDiagnostics.Core.Monitoring;

public sealed record IcmpProbeResult(
    IcmpProbeOutcome Outcome,
    DateTimeOffset Timestamp,
    long SequenceNumber,
    long StartedAtMilliseconds,
    long CompletedAtMilliseconds,
    TimeSpan? RoundTripTime,
    string? Message);
