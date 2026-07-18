using System.Net.NetworkInformation;

namespace WgbDiagnostics.Core.Monitoring;

/// <summary>
/// Preliminary .NET Ping reference implementation. This is not production-approved yet.
/// </summary>
public sealed class DotNetPingIcmpProbe : IIcmpProbe
{
    public async Task<IcmpProbeResponse> SendAsync(
        IcmpProbeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping
                .SendPingAsync(request.Target, request.TimeoutMilliseconds)
                .WaitAsync(cancellationToken);

            if (reply.Status == IPStatus.Success)
            {
                return new IcmpProbeResponse(
                    IcmpProbeOutcome.Success,
                    TimeSpan.FromMilliseconds(reply.RoundtripTime),
                    "ICMP reply received.");
            }

            return new IcmpProbeResponse(
                IcmpProbeOutcome.Loss,
                RoundTripTime: null,
                $"ICMP probe did not receive a successful reply: {reply.Status}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or ArgumentException)
        {
            return new IcmpProbeResponse(
                IcmpProbeOutcome.Error,
                RoundTripTime: null,
                $"ICMP probe failed: {ex.Message}");
        }
    }
}
