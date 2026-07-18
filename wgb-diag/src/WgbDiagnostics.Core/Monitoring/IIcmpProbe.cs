namespace WgbDiagnostics.Core.Monitoring;

public interface IIcmpProbe
{
    Task<IcmpProbeResponse> SendAsync(
        IcmpProbeRequest request,
        CancellationToken cancellationToken);
}
