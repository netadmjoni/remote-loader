using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace WgbDiagnostics.Core.Wgb;

public sealed class SshNetWgbCommandClient : IWgbCommandClient, IWgbPersistentCommandClient
{
    private static readonly TimeSpan DrainQuietPeriod = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan ReadPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ResyncTimeoutCap = TimeSpan.FromSeconds(2);
    private static readonly Regex AnsiSequencePattern = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PaginationPattern = new(
        @"-{2,}\s*More\s*-{2,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly WgbSshShellOptions[] ShellOptions =
    [
        WgbSshShellOptions.MinimalPty,
        WgbSshShellOptions.NoPty
    ];

    private readonly IWgbSshSessionFactory _sessionFactory;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private PersistentWgbSession? _persistentSession;

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
        var result = await ExecuteCommandWithDiagnosticsAsync(request, cancellationToken);
        return result.RawOutput;
    }

    public async Task<WgbCommandExecutionResult> ExecuteCommandWithDiagnosticsAsync(
        WgbCommandRequest request,
        CancellationToken cancellationToken)
    {
        var state = new WgbCommandExecutionState(request.UseEnableMode);
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            try
            {
                return await Task
                    .Run(() => ExecutePersistentCommand(request, state, cancellationToken), cancellationToken)
                    .WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ClosePersistentSession();
                throw;
            }
            catch (WgbCommandException ex) when (ex.Diagnostics is not null)
            {
                throw;
            }
            catch (TimeoutException ex)
            {
                ClosePersistentSession();
                state.Record(WgbPollEventKind.SessionLost, "WGB SSH command timed out.");
                throw CreateFailure(state, "WGB SSH command timed out.", ex);
            }
            catch (SshAuthenticationException ex)
            {
                ClosePersistentSession();
                throw CreateFailure(state, "WGB SSH authentication failed.", ex);
            }
            catch (WgbCommandException ex)
            {
                throw CreateFailure(state, ex.Message, ex);
            }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                ClosePersistentSession();
                state.Record(WgbPollEventKind.SessionLost, "WGB SSH session was lost.");
                throw CreateFailure(state, $"WGB SSH command failed: {ex.Message}", ex);
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task ResetSessionAsync(CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            ClosePersistentSession();
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private WgbCommandExecutionResult ExecutePersistentCommand(
        WgbCommandRequest request,
        WgbCommandExecutionState state,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1, request.TimeoutMilliseconds));
        var persistentSession = EnsurePersistentSession(request, state, timeout, cancellationToken);
        var output = ExecuteCommandInPersistentShell(persistentSession, request, state, timeout, cancellationToken);
        return new WgbCommandExecutionResult(output, state.ToDiagnostics());
    }

    private PersistentWgbSession EnsurePersistentSession(
        WgbCommandRequest request,
        WgbCommandExecutionState state,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var key = WgbCommandSessionKey.FromRequest(request);
        if (_persistentSession is not null
            && _persistentSession.Key == key
            && _persistentSession.Session.IsConnected)
        {
            return _persistentSession;
        }

        ClosePersistentSession();
        WgbCommandException? lastFailure = null;

        foreach (var shellOptions in ShellOptions)
        {
            IWgbSshSession? session = null;
            IWgbSshShell? shell = null;

            try
            {
                session = _sessionFactory.Create(request);
                session.Connect(cancellationToken);
                state.ConnectionSucceeded = true;

                shell = session.CreateShell(shellOptions, cancellationToken);
                var initialRead = ReadUntilPrompt(shell, expectedPrompt: null, timeout, cancellationToken);
                ThrowIfTtyInitializationError(initialRead.Output, state, "SSH shell terminal initialization failed.");

                if (!initialRead.PromptDetected || string.IsNullOrWhiteSpace(initialRead.Prompt))
                {
                    throw CreateFailure(state, "SSH prompt not detected.");
                }

                var persistentSession = new PersistentWgbSession(key, session, shell, shellOptions, initialRead.Prompt);
                state.FinalPromptConfirmed = true;
                state.Record(WgbPollEventKind.PromptDetected, $"Prompt detected: {initialRead.Prompt}");

                try
                {
                    CompleteEnableFlowIfNeeded(persistentSession, request, state, timeout, cancellationToken);
                    _persistentSession = persistentSession;
                    return persistentSession;
                }
                catch
                {
                    persistentSession.Dispose();
                    throw;
                }
            }
            catch (WgbCommandException ex) when (ShouldTryNoPtyFallback(shellOptions, ex))
            {
                shell?.Dispose();
                session?.Dispose();
                lastFailure = ex;
            }
            catch
            {
                shell?.Dispose();
                session?.Dispose();
                throw;
            }
        }

        throw lastFailure ?? CreateFailure(state, "SSH shell could not be opened.");
    }

    private static void CompleteEnableFlowIfNeeded(
        PersistentWgbSession persistentSession,
        WgbCommandRequest request,
        WgbCommandExecutionState state,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!request.UseEnableMode)
        {
            return;
        }

        if (persistentSession.Prompt.EndsWith("#", StringComparison.Ordinal))
        {
            state.EnableSucceeded = true;
            state.Record(WgbPollEventKind.EnableSucceeded, "Enable mode already active.");
            return;
        }

        state.EnableAttempted = true;
        persistentSession.Shell.WriteLine(
            string.IsNullOrWhiteSpace(request.EnableCommand) ? "enable" : request.EnableCommand.Trim(),
            cancellationToken);
        var enableOutput = ReadUntilPromptOrPassword(
            persistentSession.Shell,
            expectedPrompt: null,
            timeout,
            cancellationToken);
        ThrowIfTtyInitializationError(enableOutput.Output, state, "SSH shell terminal initialization failed after enable command.");

        if (enableOutput.PasswordPromptDetected)
        {
            if (string.IsNullOrWhiteSpace(request.EnablePassword))
            {
                throw CreateFailure(state, "Enable password rejected: enable password prompt was returned but no enable password was supplied.");
            }

            persistentSession.Shell.WriteLine(request.EnablePassword, cancellationToken);
            enableOutput = ReadUntilPrompt(
                persistentSession.Shell,
                expectedPrompt: null,
                timeout,
                cancellationToken);
            ThrowIfTtyInitializationError(enableOutput.Output, state, "SSH shell terminal initialization failed after enable password.");
        }

        if (ContainsAuthenticationFailure(enableOutput.Output))
        {
            throw CreateFailure(state, "Enable password rejected.");
        }

        if (!enableOutput.PromptDetected || string.IsNullOrWhiteSpace(enableOutput.Prompt))
        {
            throw CreateFailure(state, "SSH prompt not detected after enable command.");
        }

        if (!enableOutput.Prompt.EndsWith("#", StringComparison.Ordinal))
        {
            throw CreateFailure(state, "Enable mode did not reach a privileged prompt.");
        }

        persistentSession.Prompt = enableOutput.Prompt;
        state.EnableSucceeded = true;
        state.FinalPromptConfirmed = true;
        state.Record(WgbPollEventKind.PromptDetected, $"Privileged prompt detected: {enableOutput.Prompt}");
        state.Record(WgbPollEventKind.EnableSucceeded, "Enable mode succeeded.");
    }

    private string ExecuteCommandInPersistentShell(
        PersistentWgbSession persistentSession,
        WgbCommandRequest request,
        WgbCommandExecutionState state,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!persistentSession.Session.IsConnected)
        {
            ClosePersistentSession();
            state.Record(WgbPollEventKind.SessionLost, "WGB SSH session disconnected before command.");
            throw CreateFailure(state, "WGB SSH session disconnected before command.");
        }

        DrainStaleOutput(persistentSession.Shell, cancellationToken);

        state.CommandExecuted = true;
        state.Record(WgbPollEventKind.CommandStarted, $"WGB command started: {request.Command}");
        persistentSession.Shell.WriteLine(request.Command, cancellationToken);

        var commandRead = ReadUntilPrompt(
            persistentSession.Shell,
            persistentSession.Prompt,
            timeout,
            cancellationToken);
        ThrowIfTtyInitializationError(commandRead.Output, state, "SSH shell terminal initialization failed while running WGB command.");
        ThrowIfCommandReturnedError(commandRead.Output, state);

        var output = ExtractCommandOutput(commandRead.Output, request.Command, persistentSession.Prompt);
        if (string.IsNullOrWhiteSpace(output))
        {
            throw CreateFailure(state, "WGB command returned empty output.");
        }

        ValidateUsefulCommandOutput(output, state);
        state.Record(WgbPollEventKind.CommandOutputReceived, "WGB command output received.");

        if (commandRead.PromptDetected)
        {
            persistentSession.Prompt = string.IsNullOrWhiteSpace(commandRead.Prompt)
                ? persistentSession.Prompt
                : commandRead.Prompt;
            state.FinalPromptConfirmed = true;
            state.Record(WgbPollEventKind.CommandCompleted, "WGB command completed.");
            return output;
        }

        state.Warning = "Command output received but final prompt not confirmed.";
        state.Record(WgbPollEventKind.CommandWarning, state.Warning);

        if (TryResynchronizePrompt(persistentSession, state, timeout, cancellationToken))
        {
            return output;
        }

        state.Record(WgbPollEventKind.SessionLost, "WGB SSH session marked corrupt after prompt resync failed.");
        ClosePersistentSession();
        return output;
    }

    private static bool TryResynchronizePrompt(
        PersistentWgbSession persistentSession,
        WgbCommandExecutionState state,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        state.PromptResyncAttempted = true;
        state.Record(WgbPollEventKind.PromptResyncStarted, "Prompt resync started.");
        persistentSession.Shell.WriteLine("", cancellationToken);

        var timeout = commandTimeout < ResyncTimeoutCap ? commandTimeout : ResyncTimeoutCap;
        var read = ReadUntilPrompt(
            persistentSession.Shell,
            persistentSession.Prompt,
            timeout,
            cancellationToken);

        if (read.PromptDetected && !string.IsNullOrWhiteSpace(read.Prompt))
        {
            persistentSession.Prompt = read.Prompt;
            state.PromptResyncSucceeded = true;
            state.Record(WgbPollEventKind.PromptResyncSucceeded, "Prompt resync succeeded.");
            return true;
        }

        state.Record(WgbPollEventKind.PromptResyncFailed, "Prompt resync failed.");
        return false;
    }

    private static ShellReadResult ReadUntilPrompt(
        IWgbSshShell shell,
        string? expectedPrompt,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return ReadUntil(shell, expectedPrompt, timeout, stopOnPasswordPrompt: false, cancellationToken);
    }

    private static ShellReadResult ReadUntilPromptOrPassword(
        IWgbSshShell shell,
        string? expectedPrompt,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return ReadUntil(shell, expectedPrompt, timeout, stopOnPasswordPrompt: true, cancellationToken);
    }

    private static ShellReadResult ReadUntil(
        IWgbSshShell shell,
        string? expectedPrompt,
        TimeSpan timeout,
        bool stopOnPasswordPrompt,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var deadline = GetDeadline(timeout);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = NormalizeTerminalOutput(shell.ReadAvailable(cancellationToken));
            if (chunk.Length > 0)
            {
                output.Append(chunk);
                HandlePagination(shell, output, cancellationToken);
                var text = output.ToString();

                if (stopOnPasswordPrompt && ContainsPasswordPrompt(text))
                {
                    return new ShellReadResult(text, PromptDetected: false, Prompt: null, PasswordPromptDetected: true);
                }

                if (TryDetectPrompt(text, expectedPrompt, out var prompt))
                {
                    return new ShellReadResult(text, PromptDetected: true, prompt, PasswordPromptDetected: false);
                }
            }

            Thread.Sleep(ReadPollInterval);
        }

        return new ShellReadResult(output.ToString(), PromptDetected: false, Prompt: null, PasswordPromptDetected: false);
    }

    private static void DrainStaleOutput(
        IWgbSshShell shell,
        CancellationToken cancellationToken)
    {
        var quietSince = Stopwatch.GetTimestamp();
        while (GetElapsed(quietSince) < DrainQuietPeriod)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = shell.ReadAvailable(cancellationToken);
            if (!string.IsNullOrEmpty(chunk))
            {
                quietSince = Stopwatch.GetTimestamp();
            }

            Thread.Sleep(ReadPollInterval);
        }
    }

    private static void HandlePagination(
        IWgbSshShell shell,
        StringBuilder output,
        CancellationToken cancellationToken)
    {
        var text = output.ToString();
        if (!PaginationPattern.IsMatch(text))
        {
            return;
        }

        shell.Write(" ", cancellationToken);
        output.Clear();
        output.Append(PaginationPattern.Replace(text, ""));
    }

    private static bool TryDetectPrompt(
        string output,
        string? expectedPrompt,
        out string prompt)
    {
        prompt = "";
        var normalized = NormalizeTerminalOutput(output).TrimEnd();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedPrompt)
            && normalized.EndsWith(expectedPrompt, StringComparison.Ordinal))
        {
            prompt = expectedPrompt;
            return true;
        }

        foreach (var line in NormalizeLines(normalized).Reverse())
        {
            var trimmed = line.Trim();
            if (IsPromptLine(trimmed))
            {
                prompt = trimmed;
                return true;
            }
        }

        return false;
    }

    private static bool ShouldTryNoPtyFallback(
        WgbSshShellOptions shellOptions,
        WgbCommandException exception)
    {
        if (!shellOptions.RequestPseudoTerminal)
        {
            return false;
        }

        return exception.Message.Contains("terminal initialization", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateUsefulCommandOutput(
        string output,
        WgbCommandExecutionState state)
    {
        ThrowIfTtyInitializationError(output, state, "WGB command did not return usable output; SSH shell returned terminal initialization errors.");

        var meaningfulLines = NormalizeLines(output)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !IsPromptLine(line))
            .Where(line => !IsTtyInitializationErrorLine(line))
            .ToArray();

        if (meaningfulLines.Length == 0)
        {
            throw CreateFailure(state, "WGB command returned empty output.");
        }
    }

    private static void ThrowIfTtyInitializationError(
        string output,
        WgbCommandExecutionState state,
        string message)
    {
        var ttyErrorLine = NormalizeLines(output)
            .Select(line => line.Trim())
            .FirstOrDefault(IsTtyInitializationErrorLine);

        if (ttyErrorLine is not null)
        {
            throw CreateFailure(state, $"{message} Remote output: {ttyErrorLine}");
        }
    }

    private static bool IsTtyInitializationErrorLine(string line)
    {
        return line.Equals("can't get tty settings", StringComparison.OrdinalIgnoreCase)
            || line.Equals("can't set orig mode", StringComparison.OrdinalIgnoreCase);
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

    private static void ThrowIfCommandReturnedError(
        string output,
        WgbCommandExecutionState state)
    {
        var errorLine = NormalizeLines(output)
            .Select(line => line.Trim())
            .FirstOrDefault(IsCommandErrorLine);

        if (errorLine is not null)
        {
            throw CreateFailure(state, $"WGB command returned error or unknown command: {errorLine}");
        }
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

    private static string ExtractCommandOutput(
        string output,
        string command,
        string? knownPrompt)
    {
        var normalizedOutput = PaginationPattern.Replace(NormalizeTerminalOutput(output), "");
        var lines = NormalizeLines(normalizedOutput)
            .Select(line => line.TrimEnd())
            .ToList();

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

        while (lines.Count > 0
            && (IsPromptLine(lines[^1]) || (!string.IsNullOrWhiteSpace(knownPrompt) && lines[^1].Trim().Equals(knownPrompt, StringComparison.Ordinal))))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static bool IsPromptLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length is 0 or > 128)
        {
            return false;
        }

        if (!trimmed.EndsWith(">", StringComparison.Ordinal)
            && !trimmed.EndsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        return trimmed.All(character => !char.IsControl(character))
            && !trimmed.Contains(" ", StringComparison.Ordinal);
    }

    private static string NormalizeTerminalOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return "";
        }

        var withoutAnsi = AnsiSequencePattern.Replace(output, "");
        var builder = new StringBuilder(withoutAnsi.Length);
        foreach (var character in withoutAnsi)
        {
            if (character == '\b')
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (character == '\0')
            {
                continue;
            }

            if (char.IsControl(character)
                && character is not '\r' and not '\n' and not '\t')
            {
                continue;
            }

            builder.Append(character);
        }

        return builder
            .ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static IEnumerable<string> NormalizeLines(string output)
    {
        return NormalizeTerminalOutput(output)
            .Split('\n', StringSplitOptions.None);
    }

    private void ClosePersistentSession()
    {
        var session = _persistentSession;
        _persistentSession = null;
        session?.Dispose();
    }

    private static WgbCommandException CreateFailure(
        WgbCommandExecutionState state,
        string message)
    {
        state.FailureReason = message;
        return new WgbCommandException(message, state.ToDiagnostics());
    }

    private static WgbCommandException CreateFailure(
        WgbCommandExecutionState state,
        string message,
        Exception innerException)
    {
        state.FailureReason = message;
        return new WgbCommandException(message, state.ToDiagnostics(), innerException);
    }

    private sealed class WgbCommandExecutionState
    {
        private readonly List<WgbCommandDiagnosticEvent> _events = [];

        public WgbCommandExecutionState(bool enableAttempted)
        {
            EnableAttempted = enableAttempted;
        }

        public bool ConnectionSucceeded { get; set; }

        public bool EnableAttempted { get; set; }

        public bool EnableSucceeded { get; set; }

        public bool CommandExecuted { get; set; }

        public bool FinalPromptConfirmed { get; set; }

        public bool PromptResyncAttempted { get; set; }

        public bool PromptResyncSucceeded { get; set; }

        public string? Warning { get; set; }

        public string? FailureReason { get; set; }

        public void Record(WgbPollEventKind kind, string? message = null)
        {
            _events.Add(new WgbCommandDiagnosticEvent(kind, DateTimeOffset.UtcNow, message));
        }

        public WgbCommandExecutionDiagnostics ToDiagnostics()
        {
            return new WgbCommandExecutionDiagnostics(
                ConnectionSucceeded,
                EnableAttempted,
                EnableSucceeded,
                CommandExecuted,
                FinalPromptConfirmed,
                PromptResyncAttempted,
                PromptResyncSucceeded,
                Warning,
                FailureReason,
                _events.ToArray());
        }
    }

    private sealed record WgbCommandSessionKey(
        string Address,
        int Port,
        string Username,
        bool UseEnableMode,
        string EnableCommand)
    {
        public static WgbCommandSessionKey FromRequest(WgbCommandRequest request)
        {
            return new WgbCommandSessionKey(
                request.Address,
                request.Port,
                request.Username,
                request.UseEnableMode,
                request.EnableCommand);
        }
    }

    private sealed class PersistentWgbSession : IDisposable
    {
        public PersistentWgbSession(
            WgbCommandSessionKey key,
            IWgbSshSession session,
            IWgbSshShell shell,
            WgbSshShellOptions shellOptions,
            string prompt)
        {
            Key = key;
            Session = session;
            Shell = shell;
            ShellOptions = shellOptions;
            Prompt = prompt;
        }

        public WgbCommandSessionKey Key { get; }

        public IWgbSshSession Session { get; }

        public IWgbSshShell Shell { get; }

        public WgbSshShellOptions ShellOptions { get; }

        public string Prompt { get; set; }

        public void Dispose()
        {
            Shell.Dispose();
            if (Session.IsConnected)
            {
                Session.Disconnect();
            }

            Session.Dispose();
        }
    }

    private sealed record ShellReadResult(
        string Output,
        bool PromptDetected,
        string? Prompt,
        bool PasswordPromptDetected = false);

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

        public IWgbSshShell CreateShell(
            WgbSshShellOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return options.RequestPseudoTerminal
                ? CreatePtyShell(options)
                : CreateNoPtyShell();
        }

        public void Disconnect()
        {
            _client.Disconnect();
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        private IWgbSshShell CreatePtyShell(WgbSshShellOptions options)
        {
            var terminalModes = options.UseMinimalTerminalModes
                ? new Dictionary<TerminalModes, uint>()
                : [];
            var shell = _client.CreateShellStream(
                string.IsNullOrWhiteSpace(options.TerminalName) ? "vt100" : options.TerminalName,
                options.Columns,
                options.Rows,
                options.Width,
                options.Height,
                options.BufferSize,
                terminalModes);
            return new SshNetShellStreamWgbShell(shell);
        }

        private IWgbSshShell CreateNoPtyShell()
        {
            var input = new BlockingInputStream();
            var output = new CapturingOutputStream();
            var error = new CapturingOutputStream();
            var shell = _client.CreateShell(input, output, error);
            shell.Start();
            return new SshNetNoPtyWgbShell(shell, input, output, error);
        }
    }

    private sealed class SshNetShellStreamWgbShell : IWgbSshShell
    {
        private readonly ShellStream _shell;

        public SshNetShellStreamWgbShell(ShellStream shell)
        {
            _shell = shell;
        }

        public string ReadAvailable(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = new StringBuilder();
            while (_shell.DataAvailable)
            {
                output.Append(_shell.Read());
            }

            return output.ToString();
        }

        public void Write(
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _shell.Write(text);
            _shell.Flush();
        }

        public void WriteLine(
            string line,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _shell.WriteLine(line);
            _shell.Flush();
        }

        public void Dispose()
        {
            _shell.Dispose();
        }
    }

    private sealed class SshNetNoPtyWgbShell : IWgbSshShell
    {
        private static readonly Encoding OutputEncoding = Encoding.UTF8;
        private readonly Shell _shell;
        private readonly BlockingInputStream _input;
        private readonly CapturingOutputStream _output;
        private readonly CapturingOutputStream _error;

        public SshNetNoPtyWgbShell(
            Shell shell,
            BlockingInputStream input,
            CapturingOutputStream output,
            CapturingOutputStream error)
        {
            _shell = shell;
            _input = input;
            _output = output;
            _error = error;
        }

        public string ReadAvailable(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _output.Drain(OutputEncoding) + _error.Drain(OutputEncoding);
        }

        public void Write(
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = OutputEncoding.GetBytes(text);
            _input.Write(bytes, 0, bytes.Length);
        }

        public void WriteLine(
            string line,
            CancellationToken cancellationToken)
        {
            Write(line + "\n", cancellationToken);
        }

        public void Dispose()
        {
            _input.Dispose();
            _shell.Stop();
            _shell.Dispose();
            _output.Dispose();
            _error.Dispose();
        }
    }

    private sealed class BlockingInputStream : Stream
    {
        private readonly object _sync = new();
        private readonly Queue<byte> _buffer = [];
        private bool _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                while (_buffer.Count == 0 && !_disposed)
                {
                    Monitor.Wait(_sync, 100);
                }

                if (_buffer.Count == 0 && _disposed)
                {
                    return 0;
                }

                var bytesRead = Math.Min(count, _buffer.Count);
                for (var index = 0; index < bytesRead; index++)
                {
                    buffer[offset + index] = _buffer.Dequeue();
                }

                return bytesRead;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                for (var index = 0; index < count; index++)
                {
                    _buffer.Enqueue(buffer[offset + index]);
                }

                Monitor.PulseAll(_sync);
            }
        }

        protected override void Dispose(bool disposing)
        {
            lock (_sync)
            {
                _disposed = true;
                Monitor.PulseAll(_sync);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CapturingOutputStream : Stream
    {
        private readonly object _sync = new();
        private readonly List<byte> _buffer = [];

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public string Drain(Encoding encoding)
        {
            lock (_sync)
            {
                if (_buffer.Count == 0)
                {
                    return "";
                }

                var bytes = _buffer.ToArray();
                _buffer.Clear();
                return encoding.GetString(bytes);
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                for (var index = 0; index < count; index++)
                {
                    _buffer.Add(buffer[offset + index]);
                }
            }
        }
    }

    private static long GetDeadline(TimeSpan timeout)
    {
        return Stopwatch.GetTimestamp() + timeout.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
    }

    private static TimeSpan GetElapsed(long startedAt)
    {
        var ticks = Stopwatch.GetTimestamp() - startedAt;
        return TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }
}
