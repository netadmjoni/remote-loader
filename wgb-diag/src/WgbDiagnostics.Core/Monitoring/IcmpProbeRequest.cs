namespace WgbDiagnostics.Core.Monitoring;

public sealed record IcmpProbeRequest(
    string Target,
    int TimeoutMilliseconds,
    long SequenceNumber,
    long StartedAtMilliseconds);
