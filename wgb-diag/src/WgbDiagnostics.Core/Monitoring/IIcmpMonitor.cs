namespace WgbDiagnostics.Core.Monitoring;

public interface IIcmpMonitor
{
    Task RunAsync(
        IcmpMonitorOptions options,
        Func<IcmpMonitorEvent, ValueTask> onEvent,
        CancellationToken cancellationToken);
}
