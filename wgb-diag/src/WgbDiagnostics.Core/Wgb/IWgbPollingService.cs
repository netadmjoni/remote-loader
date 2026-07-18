namespace WgbDiagnostics.Core.Wgb;

public interface IWgbPollingService
{
    Task RunAsync(
        WgbPollingOptions options,
        Func<WgbPollEvent, ValueTask> onEvent,
        CancellationToken cancellationToken);
}
