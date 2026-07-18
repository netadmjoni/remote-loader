namespace WgbDiagnostics.Core.Wgb;

public interface IWgbCommandClient
{
    Task<string> ExecuteCommandAsync(
        WgbCommandRequest request,
        CancellationToken cancellationToken);
}
