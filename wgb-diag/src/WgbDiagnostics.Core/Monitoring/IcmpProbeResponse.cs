namespace WgbDiagnostics.Core.Monitoring;

public sealed record IcmpProbeResponse(
    IcmpProbeOutcome Outcome,
    TimeSpan? RoundTripTime,
    string? Message);
