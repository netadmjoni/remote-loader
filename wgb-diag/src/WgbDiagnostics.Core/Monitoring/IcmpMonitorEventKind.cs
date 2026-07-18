namespace WgbDiagnostics.Core.Monitoring;

public enum IcmpMonitorEventKind
{
    PingReply,
    Loss,
    LossStarted,
    AlertThresholdReached,
    Recovered,
    Error
}
