using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace WgbDiagnostics.Core.Wgb;

public sealed class SshNetWgbCommandClient : IWgbCommandClient
{
    public async Task<string> ExecuteCommandAsync(
        WgbCommandRequest request,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(Math.Max(1, request.TimeoutMilliseconds)));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            return await Task
                .Run(() => ExecuteCommand(request, linkedCancellation.Token), linkedCancellation.Token)
                .WaitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException("WGB SSH command timed out.", ex);
        }
        catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            throw new WgbCommandException($"WGB SSH command failed: {ex.Message}", ex);
        }
    }

    private static string ExecuteCommand(
        WgbCommandRequest request,
        CancellationToken cancellationToken)
    {
        var connectionInfo = new PasswordConnectionInfo(
            request.Address,
            request.Port,
            request.Username,
            request.Password)
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1, request.TimeoutMilliseconds))
        };

        using var client = new SshClient(connectionInfo);
        using var cancellationRegistration = cancellationToken.Register(client.Dispose);

        client.Connect();
        cancellationToken.ThrowIfCancellationRequested();

        using var command = client.CreateCommand(request.Command);
        command.CommandTimeout = TimeSpan.FromMilliseconds(Math.Max(1, request.TimeoutMilliseconds));
        var output = command.Execute();

        if (command.ExitStatus != 0)
        {
            var error = string.IsNullOrWhiteSpace(command.Error)
                ? $"Remote command exited with status {command.ExitStatus}."
                : command.Error.Trim();
            throw new WgbCommandException(error);
        }

        if (client.IsConnected)
        {
            client.Disconnect();
        }

        return output;
    }
}
