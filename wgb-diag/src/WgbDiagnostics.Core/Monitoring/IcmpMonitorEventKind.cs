namespace WgbDiagnostics.Core.Monitoring;

public enum IcmpMonitorEventKind
{
    PingReply,
    PacketLoss,
    Loss,
    LossStarted,
    AlertThresholdReached,
    Recovered,
    Error
}
