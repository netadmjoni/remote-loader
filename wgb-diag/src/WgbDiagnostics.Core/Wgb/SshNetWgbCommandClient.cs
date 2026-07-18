using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace WgbDiagnostics.Core.Wgb;

public sealed class SshNetWgbCommandClient : IWgbCommandClient
{
    private static readonly TimeSpan ShellQuietPeriod = TimeSpan.FromMilliseconds(150);
    private readonly IWgbSshSessionFactory _sessionFactory;

    public SshNetWgbCommandClient()
        : this(new SshNetWgbSessionFactory())
    {
    }

    public SshNetWgbCommandClient(IWgbSshSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory;
    }

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
        catch (SshAuthenticationException ex)
        {
            throw new WgbCommandException("WGB SSH authentication failed.", ex);
        }
        catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            throw new WgbCommandException($"WGB SSH command failed: {ex.Message}", ex);
        }
    }

    private string ExecuteCommand(
        WgbCommandRequest request,
        CancellationToken cancellationToken)
    {
        using var session = _sessionFactory.Create(request);
        using var cancellationRegistration = cancellationToken.Register(session.Dispose);
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1, request.TimeoutMilliseconds));

        session.Connect(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return request.UseEnableMode
                ? ExecuteCommandInEnableMode(session, request, timeout, cancellationToken)
                : session.ExecuteCommand(request.Command, timeout, cancellationToken);
        }
        finally
        {
            if (session.IsConnected)
            {
                session.Disconnect();
            }
        }
    }

    private static string ExecuteCommandInEnableMode(
        IWgbSshSession session,
        WgbCommandRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var shell = session.CreateShell(cancellationToken);
        var initialOutput = shell.Read(timeout, cancellationToken);
        if (!TryGetLastPrompt(initialOutput, out var prompt))
        {
            throw new WgbCommandException("SSH prompt not detected.");
        }

        if (!prompt.EndsWith("#", StringComparison.Ordinal))
        {
            var enableOutput = shell.SendLine(
                string.IsNullOrWhiteSpace(request.EnableCommand) ? "enable" : request.EnableCommand.Trim(),
                timeout,
                cancellationToken);

            if (ContainsPasswordPrompt(enableOutput))
            {
                if (string.IsNullOrWhiteSpace(request.EnablePassword))
                {
                    throw new WgbCommandException("Enable password rejected: enable password prompt was returned but no enable password was supplied.");
                }

                enableOutput += shell.SendLine(request.EnablePassword, timeout, cancellationToken);
            }

            if (ContainsAuthenticationFailure(enableOutput))
            {
                throw new WgbCommandException("Enable password rejected.");
            }

            if (!TryGetLastPrompt(enableOutput, out prompt))
            {
                throw new WgbCommandException("SSH prompt not detected after enable command.");
            }

            if (!prompt.EndsWith("#", StringComparison.Ordinal))
            {
                throw new WgbCommandException("Enable mode did not reach a privileged prompt.");
            }
        }

        var commandOutput = shell.SendLine(request.Command, timeout, cancellationToken);
        if (!TryGetLastPrompt(commandOutput, out _))
        {
            throw new WgbCommandException("SSH prompt not detected after WGB command.");
        }

        ThrowIfCommandReturnedError(commandOutput);
        return ExtractCommandOutput(commandOutput, request.Command);
    }

    private static bool TryGetLastPrompt(string output, out string prompt)
    {
        prompt = "";
        foreach (var line in NormalizeLines(output).Reverse())
        {
            var trimmed = line.Trim();
            if (trimmed.EndsWith(">", StringComparison.Ordinal) || trimmed.EndsWith("#", StringComparison.Ordinal))
            {
                prompt = trimmed;
                return true;
            }
        }

        return false;
    }

    private static bool ContainsPasswordPrompt(string output)
    {
        return output.Contains("password:", StringComparison.OrdinalIgnoreCase)
            || output.Contains("password :", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAuthenticationFailure(string output)
    {
        return output.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || output.Contains("incorrect", StringComparison.OrdinalIgnoreCase)
            || output.Contains("invalid password", StringComparison.OrdinalIgnoreCase)
            || output.Contains("bad password", StringComparison.OrdinalIgnoreCase)
            || output.Contains("denied", StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIfCommandReturnedError(string output)
    {
        var errorLine = NormalizeLines(output)
            .Select(line => line.Trim())
            .FirstOrDefault(IsCommandErrorLine);

        if (errorLine is not null)
        {
            throw new WgbCommandException($"WGB command returned error or unknown command: {errorLine}");
        }
    }

    private static bool IsCommandErrorLine(string line)
    {
        return line.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            || line.Contains("invalid input", StringComparison.OrdinalIgnoreCase)
            || line.Contains("incomplete command", StringComparison.OrdinalIgnoreCase)
            || line.Contains("ambiguous command", StringComparison.OrdinalIgnoreCase)
            || line.Contains("% unknown", StringComparison.OrdinalIgnoreCase)
            || line.Contains("% invalid", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractCommandOutput(string output, string command)
    {
        var lines = NormalizeLines(output).ToList();

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > 0)
        {
            var firstLine = lines[0].Trim();
            if (firstLine.Equals(command, StringComparison.OrdinalIgnoreCase)
                || firstLine.EndsWith(command, StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(0);
            }
        }

        while (lines.Count > 0 && IsPromptLine(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static bool IsPromptLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.EndsWith(">", StringComparison.Ordinal)
            || trimmed.EndsWith("#", StringComparison.Ordinal);
    }

    private static IEnumerable<string> NormalizeLines(string output)
    {
        return output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None);
    }

    private sealed class SshNetWgbSessionFactory : IWgbSshSessionFactory
    {
        public IWgbSshSession Create(WgbCommandRequest request)
        {
            var connectionInfo = new PasswordConnectionInfo(
                request.Address,
                request.Port,
                request.Username,
                request.Password)
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Max(1, request.TimeoutMilliseconds))
            };

            return new SshNetWgbSession(connectionInfo);
        }
    }

    private sealed class SshNetWgbSession : IWgbSshSession
    {
        private readonly SshClient _client;

        public SshNetWgbSession(ConnectionInfo connectionInfo)
        {
            _client = new SshClient(connectionInfo);
        }

        public bool IsConnected => _client.IsConnected;

        public void Connect(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _client.Connect();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public string ExecuteCommand(
            string commandText,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var command = _client.CreateCommand(commandText);
            command.CommandTimeout = timeout;
            var output = command.Execute();
            cancellationToken.ThrowIfCancellationRequested();

            if (command.ExitStatus != 0)
            {
                var error = string.IsNullOrWhiteSpace(command.Error)
                    ? $"Remote command exited with status {command.ExitStatus}."
                    : command.Error.Trim();
                throw new WgbCommandException($"WGB command returned error: {error}");
            }

            ThrowIfCommandReturnedError(output);
            return output;
        }

        public IWgbSshShell CreateShell(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shell = _client.CreateShellStream(
                "wgb-diagnostics",
                columns: 120,
                rows: 40,
                width: 0,
                height: 0,
                bufferSize: 4096);
            return new SshNetWgbShell(shell);
        }

        public void Disconnect()
        {
            _client.Disconnect();
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }

    private sealed class SshNetWgbShell : IWgbSshShell
    {
        private readonly ShellStream _shell;

        public SshNetWgbShell(ShellStream shell)
        {
            _shell = shell;
        }

        public string Read(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return ReadUntilQuiet(timeout, cancellationToken);
        }

        public string SendLine(
            string line,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _shell.WriteLine(line);
            return ReadUntilQuiet(timeout, cancellationToken);
        }

        public void Dispose()
        {
            _shell.Dispose();
        }

        private string ReadUntilQuiet(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var output = new StringBuilder();
            var deadline = Stopwatch.GetTimestamp() + timeout.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
            var lastDataAt = Stopwatch.GetTimestamp();
            var sawData = false;

            while (Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                while (_shell.DataAvailable)
                {
                    output.Append(_shell.Read());
                    sawData = true;
                    lastDataAt = Stopwatch.GetTimestamp();
                }

                if (sawData && GetElapsed(lastDataAt) >= ShellQuietPeriod)
                {
                    return output.ToString();
                }

                Thread.Sleep(25);
            }

            throw new TimeoutException("WGB SSH command timed out.");
        }

        private static TimeSpan GetElapsed(long startedAt)
        {
            var ticks = Stopwatch.GetTimestamp() - startedAt;
            return TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
        }
    }
}
