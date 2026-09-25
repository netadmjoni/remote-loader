using Renci.SshNet.Common;
using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class WgbCommandClientEnableModeTests
{
    [Fact]
    public async Task NoEnableModeUsesPersistentShell()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(
            "WGB#",
            [
                "show wgb dot11 associations\r\nParent AP Name: AP-A\r\nWGB#"
            ])));
        var client = new SshNetWgbCommandClient(factory);

        var output = await client.ExecuteCommandAsync(CreateRequest(useEnableMode: false), CancellationToken.None);

        Assert.Contains("AP-A", output);
        Assert.Equal("show wgb dot11 associations", factory.Sessions.Single().ShellLines.Single());
        Assert.Empty(factory.Sessions.Single().DirectCommands);
    }

    [Fact]
    public async Task EnableModeWithoutPasswordRunsCommandFromPrivilegedPrompt()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(
            "WGB>",
            [
                "enable\r\nWGB#",
                "show wgb dot11 associations\r\nParent AP Name: AP-A\r\nWGB#"
            ])));
        var client = new SshNetWgbCommandClient(factory);

        var output = await client.ExecuteCommandAsync(CreateRequest(useEnableMode: true), CancellationToken.None);

        Assert.Equal("Parent AP Name: AP-A", output);
        Assert.Equal(new[] { "enable", "show wgb dot11 associations" }, factory.Sessions.Single().ShellLines);
    }

    [Fact]
    public async Task EnableModeUsesMinimalPtyFirst()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(
            "WGB#",
            [
                "show wgb dot11 associations\r\nParent AP Name: AP-A\r\nWGB#"
            ])));
        var client = new SshNetWgbCommandClient(factory);

        await client.ExecuteCommandAsync(CreateRequest(useEnableMode: true), CancellationToken.None);

        var options = Assert.Single(factory.Sessions.Single().ShellOptions);
        Assert.True(options.RequestPseudoTerminal);
        Assert.Equal("vt100", options.TerminalName);
        Assert.True(options.UseMinimalTerminalModes);
    }

    [Fact]
    public async Task EnableModeWithPasswordHandlesPasswordPrompt()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(
            "WGB>",
            [
                "Password:",
                "WGB#",
                "show wgb dot11 associations\r\nRSSI: -61\r\nWGB#"
            ])));
        var client = new SshNetWgbCommandClient(factory);

        var output = await client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true, enablePassword: "enable-secret"),
            CancellationToken.None);

        Assert.Equal("RSSI: -61", output);
        Assert.Equal(new[] { "enable", "enable-secret", "show wgb dot11 associations" }, factory.Sessions.Single().ShellLines);
    }

    [Fact]
    public async Task EnablePasswordRejectedReturnsClearError()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(
            "WGB>",
            [
                "Password:",
                "Access denied\r\nWGB>"
            ])));
        var client = new SshNetWgbCommandClient(factory);

        var ex = await Assert.ThrowsAsync<WgbCommandException>(() => client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true, enablePassword: "wrong-secret"),
            CancellationToken.None));

        Assert.Contains("Enable password rejected", ex.Message);
    }

    [Fact]
    public async Task UnknownPromptReturnsClearError()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan("Welcome to WGB\r\n", [])));
        var client = new SshNetWgbCommandClient(factory);

        var ex = await Assert.ThrowsAsync<WgbCommandException>(() => client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true, timeoutMilliseconds: 100),
            CancellationToken.None));

        Assert.Contains("prompt not detected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShellTimeoutReturnsCommandTimeout()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(new TimeoutException("scripted timeout"), [])));
        var client = new SshNetWgbCommandClient(factory);

        var ex = await Assert.ThrowsAsync<WgbCommandException>(() => client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true),
            CancellationToken.None));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TtyInitializationErrorReturnsClearFailure()
    {
        var factory = new FakeSessionFactory(
            new FakeSessionPlan(new FakeShellPlan("can't get tty settings\r\ncan't set orig mode\r\n", [])),
            new FakeSessionPlan(new FakeShellPlan("can't get tty settings\r\ncan't set orig mode\r\n", [])));
        var client = new SshNetWgbCommandClient(factory);

        var ex = await Assert.ThrowsAsync<WgbCommandException>(() => client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true),
            CancellationToken.None));

        Assert.Contains("terminal initialization", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.Diagnostics?.ConnectionSucceeded);
        Assert.False(ex.Diagnostics?.CommandExecuted);
    }

    [Fact]
    public async Task ShellCanFallBackToNoPty()
    {
        var factory = new FakeSessionFactory(
            new FakeSessionPlan(new FakeShellPlan("can't get tty settings\r\ncan't set orig mode\r\n", [])),
            new FakeSessionPlan(new FakeShellPlan(
                "WGB>",
                [
                    "enable\r\nWGB#",
                    "show wgb dot11 associations\r\nParent AP Name: AP-A\r\nWGB#"
                ])));
        var client = new SshNetWgbCommandClient(factory);

        var output = await client.ExecuteCommandAsync(CreateRequest(useEnableMode: true), CancellationToken.None);

        Assert.Equal("Parent AP Name: AP-A", output);
        Assert.Equal(new[] { true, false }, factory.Sessions.SelectMany(session => session.ShellOptions).Select(options => options.RequestPseudoTerminal).ToArray());
    }

    [Fact]
    public async Task EmptyCommandOutputFails()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(
            "WGB#",
            [
                "show wgb dot11 associations\r\nWGB#"
            ])));
        var client = new SshNetWgbCommandClient(factory);

        var ex = await Assert.ThrowsAsync<WgbCommandException>(() => client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true),
            CancellationToken.None));

        Assert.Contains("empty output", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.Diagnostics?.CommandExecuted);
    }

    [Fact]
    public async Task TtyOnlyCommandOutputFails()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(
            "WGB#",
            [
                "show wgb dot11 associations\r\ncan't get tty settings\r\ncan't set orig mode\r\nWGB#"
            ])));
        var client = new SshNetWgbCommandClient(factory);

        var ex = await Assert.ThrowsAsync<WgbCommandException>(() => client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true),
            CancellationToken.None));

        Assert.Contains("terminal initialization", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ex.Diagnostics?.CommandExecuted);
    }

    [Fact]
    public async Task DiagnosticsReportSuccessfulConnectionEnableAndCommand()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(
            "WGB>",
            [
                "Password:",
                "WGB#",
                "show wgb dot11 associations\r\nRSSI: -61\r\nWGB#"
            ])));
        var client = new SshNetWgbCommandClient(factory);

        var result = await client.ExecuteCommandWithDiagnosticsAsync(
            CreateRequest(useEnableMode: true, enablePassword: "enable-secret"),
            CancellationToken.None);

        Assert.Equal("RSSI: -61", result.RawOutput);
        Assert.True(result.Diagnostics.ConnectionSucceeded);
        Assert.True(result.Diagnostics.EnableAttempted);
        Assert.True(result.Diagnostics.EnableSucceeded);
        Assert.True(result.Diagnostics.CommandExecuted);
        Assert.True(result.Diagnostics.FinalPromptConfirmed);
        Assert.Contains(result.Diagnostics.Events, item => item.Message?.StartsWith("SSH_CONNECT_START host=192.0.2.10 port=22 user=admin auth=password timeout_ms=250", StringComparison.Ordinal) == true);
        Assert.Contains(result.Diagnostics.Events, item => item.Message == "SSH_AUTH_OK");
        Assert.Contains(result.Diagnostics.Events, item => item.Kind == WgbPollEventKind.PromptDetected);
        Assert.Contains(result.Diagnostics.Events, item => item.Kind == WgbPollEventKind.CommandCompleted);
    }

    [Fact]
    public async Task TcpConnectTimeoutReportsExactConnectionStageWithoutSecrets()
    {
        var plan = new FakeSessionPlan
        {
            ConnectException = new SshOperationTimeoutException("Connection failed to establish within 5000 milliseconds.")
        };
        var factory = new FakeSessionFactory(plan);
        var client = new SshNetWgbCommandClient(factory);

        var ex = await Assert.ThrowsAsync<WgbCommandException>(() => client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true, timeoutMilliseconds: 5000),
            CancellationToken.None));

        Assert.Contains("SSH_CONNECT_FAILED stage=TCP_CONNECT", ex.Message);
        Assert.DoesNotContain("ssh-secret", ex.Message);
        Assert.Contains(ex.Diagnostics!.Events, item => item.Message?.StartsWith("SSH_CONNECT_START", StringComparison.Ordinal) == true);
        Assert.Contains(ex.Diagnostics.Events, item => item.Message?.StartsWith("SSH_CONNECT_FAILED stage=TCP_CONNECT", StringComparison.Ordinal) == true);
        Assert.True(factory.Sessions.Single().Disposed);
    }

    [Fact]
    public async Task MultipleSequentialCommandsReuseSameSshSession()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(
            "WGB#",
            [
                "show wgb dot11 associations\r\nAP=MGN1080STV-223\r\nRSSI=45\r\nchannel=11\r\nWGB#",
                "show wgb dot11 associations\r\nAP=MGN1080STV-224\r\nRSSI=44\r\nchannel=11\r\nWGB#"
            ])));
        var client = new SshNetWgbCommandClient(factory);
        var request = CreateRequest(useEnableMode: true);

        var first = await client.ExecuteCommandAsync(request, CancellationToken.None);
        var second = await client.ExecuteCommandWithDiagnosticsAsync(request, CancellationToken.None);

        Assert.Contains("MGN1080STV-223", first);
        Assert.Contains("MGN1080STV-224", second.RawOutput);
        Assert.True(second.Diagnostics.ConnectionSucceeded);
        Assert.True(second.Diagnostics.EnableSucceeded);
        Assert.Contains(second.Diagnostics.Events, item => item.Message == "SSH_SESSION_REUSED host=192.0.2.10 port=22");
        var session = Assert.Single(factory.Sessions);
        Assert.Equal(2, session.ShellLines.Count(line => line == "show wgb dot11 associations"));
    }

    [Fact]
    public async Task LeftoverEchoAndPromptAreDrainedBeforeNextCommand()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(
            "WGB#",
            [
                new[] { "show wgb dot11 associations\r\nAP=first\r\nWGB#", "show wgb dot11 associations\r\nWGB#" },
                "show wgb dot11 associations\r\nAP=second\r\nWGB#"
            ])));
        var client = new SshNetWgbCommandClient(factory);
        var request = CreateRequest(useEnableMode: true);

        await client.ExecuteCommandAsync(request, CancellationToken.None);
        var second = await client.ExecuteCommandAsync(request, CancellationToken.None);

        Assert.Equal("AP=second", second);
    }

    [Fact]
    public async Task UsefulOutputWithoutFinalPromptReturnsWarningAndReusesSessionAfterResync()
    {
        var factory = new FakeSessionFactory(new FakeSessionPlan(new FakeShellPlan(
            "WGB#",
            [
                "show wgb dot11 associations\r\nAP=MGN1080STV-223\r\nRSSI=45\r\nchannel=11\r\n",
                "WGB#",
                "show wgb dot11 associations\r\nAP=MGN1080STV-224\r\nWGB#"
            ])));
        var client = new SshNetWgbCommandClient(factory);
        var request = CreateRequest(useEnableMode: true, timeoutMilliseconds: 100);

        var first = await client.ExecuteCommandWithDiagnosticsAsync(request, CancellationToken.None);
        var second = await client.ExecuteCommandAsync(request, CancellationToken.None);

        Assert.Contains("MGN1080STV-223", first.RawOutput);
        Assert.Equal("Command output received but final prompt not confirmed.", first.Diagnostics.Warning);
        Assert.True(first.Diagnostics.PromptResyncSucceeded);
        Assert.Contains("MGN1080STV-224", second);
        Assert.Single(factory.Sessions);
    }

    [Fact]
    public async Task FailedPromptResyncReconnectsBeforeNextCommand()
    {
        var factory = new FakeSessionFactory(
            new FakeSessionPlan(new FakeShellPlan(
                "WGB#",
                [
                    "show wgb dot11 associations\r\nAP=first\r\n"
                ])),
            new FakeSessionPlan(new FakeShellPlan(
                "WGB#",
                [
                    "show wgb dot11 associations\r\nAP=second\r\nWGB#"
                ])));
        var client = new SshNetWgbCommandClient(factory);
        var request = CreateRequest(useEnableMode: true, timeoutMilliseconds: 100);

        var first = await client.ExecuteCommandWithDiagnosticsAsync(request, CancellationToken.None);
        var second = await client.ExecuteCommandAsync(request, CancellationToken.None);

        Assert.Contains("AP=first", first.RawOutput);
        Assert.False(first.Diagnostics.PromptResyncSucceeded);
        Assert.Contains("AP=second", second);
        Assert.Equal(2, factory.Sessions.Count);
        Assert.True(factory.Sessions[0].Disposed);
    }

    private static WgbCommandRequest CreateRequest(
        bool useEnableMode,
        string enablePassword = "",
        int timeoutMilliseconds = 250)
    {
        return new WgbCommandRequest(
            "192.0.2.10",
            22,
            "admin",
            "ssh-secret",
            "show wgb dot11 associations",
            timeoutMilliseconds,
            useEnableMode,
            "enable",
            enablePassword);
    }

    private sealed class FakeSessionFactory : IWgbSshSessionFactory
    {
        private readonly Queue<FakeSessionPlan> _plans;

        public FakeSessionFactory(params FakeSessionPlan[] plans)
        {
            _plans = new Queue<FakeSessionPlan>(plans);
        }

        public List<FakeSession> Sessions { get; } = [];

        public IWgbSshSession Create(WgbCommandRequest request)
        {
            var plan = _plans.Count == 0
                ? new FakeSessionPlan(new FakeShellPlan("WGB#", []))
                : _plans.Dequeue();
            var session = new FakeSession(plan);
            Sessions.Add(session);
            return session;
        }
    }

    private sealed record FakeSessionPlan(params FakeShellPlan[] ShellPlans)
    {
        public Exception? ConnectException { get; init; }
    }

    private sealed record FakeShellPlan(object InitialOutput, IReadOnlyList<object> Responses);

    private sealed class FakeSession : IWgbSshSession
    {
        private readonly Queue<FakeShellPlan> _shellPlans;
        private readonly Exception? _connectException;

        public FakeSession(FakeSessionPlan plan)
        {
            _shellPlans = new Queue<FakeShellPlan>(plan.ShellPlans);
            _connectException = plan.ConnectException;
        }

        public bool IsConnected { get; private set; }

        public bool Disposed { get; private set; }

        public List<string> DirectCommands { get; } = [];

        public List<string> ShellLines { get; } = [];

        public List<WgbSshShellOptions> ShellOptions { get; } = [];

        public void Connect(CancellationToken cancellationToken)
        {
            if (_connectException is not null)
            {
                throw _connectException;
            }

            IsConnected = true;
        }

        public string ExecuteCommand(
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            DirectCommands.Add(command);
            return "";
        }

        public IWgbSshShell CreateShell(
            WgbSshShellOptions options,
            CancellationToken cancellationToken)
        {
            ShellOptions.Add(options);
            var plan = _shellPlans.Count == 0
                ? new FakeShellPlan("WGB#", [])
                : _shellPlans.Dequeue();
            return new FakeShell(plan, ShellLines);
        }

        public void Disconnect()
        {
            IsConnected = false;
        }

        public void Dispose()
        {
            Disposed = true;
            IsConnected = false;
        }
    }

    private sealed class FakeShell : IWgbSshShell
    {
        private readonly Queue<object> _available = [];
        private readonly Queue<object> _responses;
        private readonly List<string> _lines;

        public FakeShell(
            FakeShellPlan plan,
            List<string> lines)
        {
            _responses = new Queue<object>(plan.Responses);
            _lines = lines;
            EnqueueAvailable(plan.InitialOutput);
        }

        public string ReadAvailable(CancellationToken cancellationToken)
        {
            if (_available.Count == 0)
            {
                return "";
            }

            var item = _available.Dequeue();
            if (item is Exception ex)
            {
                throw ex;
            }

            return (string)item;
        }

        public void Write(
            string text,
            CancellationToken cancellationToken)
        {
            _lines.Add(text);
        }

        public void WriteLine(
            string line,
            CancellationToken cancellationToken)
        {
            _lines.Add(line);
            if (_responses.Count > 0)
            {
                EnqueueAvailable(_responses.Dequeue());
            }
        }

        public void Dispose()
        {
        }

        private void EnqueueAvailable(object item)
        {
            switch (item)
            {
                case string text:
                    _available.Enqueue(text);
                    break;
                case string[] chunks:
                    foreach (var chunk in chunks)
                    {
                        _available.Enqueue(chunk);
                    }

                    break;
                case Exception:
                    _available.Enqueue(item);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported fake shell response: {item.GetType().Name}");
            }
        }
    }
}
