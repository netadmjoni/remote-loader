using WgbDiagnostics.Core.Configuration;

namespace WgbDiagnostics.Core.Monitoring;

public sealed record IcmpMonitorOptions(
    string Target,
    int IntervalMilliseconds,
    int TimeoutMilliseconds,
    int LossThresholdMilliseconds)
{
    public static IcmpMonitorOptions FromDiagnosticsOptions(WgbDiagnosticsOptions options)
    {
        return new IcmpMonitorOptions(
            options.PingTarget,
            options.PingIntervalMilliseconds,
            options.PingTimeoutMilliseconds,
            options.LossThresholdMilliseconds);
    }
}
