using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class WgbCommandClientEnableModeTests
{
    [Fact]
    public async Task NoEnableModeUsesDirectCommandWithoutShell()
    {
        var factory = new FakeSessionFactory
        {
            DirectCommandOutput = "Parent AP Name: AP-A"
        };
        var client = new SshNetWgbCommandClient(factory);

        var output = await client.ExecuteCommandAsync(CreateRequest(useEnableMode: false), CancellationToken.None);

        Assert.Contains("AP-A", output);
        Assert.Equal("show wgb dot11 associations", factory.Session!.DirectCommands.Single());
        Assert.False(factory.Session.ShellCreated);
    }

    [Fact]
    public async Task EnableModeWithoutPasswordRunsCommandFromPrivilegedPrompt()
    {
        var factory = new FakeSessionFactory([
            "WGB>",
            "enable\r\nWGB#",
            "show wgb dot11 associations\r\nParent AP Name: AP-A\r\nWGB#"
        ]);
        var client = new SshNetWgbCommandClient(factory);

        var output = await client.ExecuteCommandAsync(CreateRequest(useEnableMode: true), CancellationToken.None);

        Assert.Equal("Parent AP Name: AP-A", output);
        Assert.Equal(new[] { "enable", "show wgb dot11 associations" }, factory.Session!.ShellLines);
    }

    [Fact]
    public async Task EnableModeWithPasswordHandlesPasswordPrompt()
    {
        var factory = new FakeSessionFactory([
            "WGB>",
            "Password:",
            "WGB#",
            "show wgb dot11 associations\r\nRSSI: -61\r\nWGB#"
        ]);
        var client = new SshNetWgbCommandClient(factory);

        var output = await client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true, enablePassword: "enable-secret"),
            CancellationToken.None);

        Assert.Equal("RSSI: -61", output);
        Assert.Equal(new[] { "enable", "enable-secret", "show wgb dot11 associations" }, factory.Session!.ShellLines);
    }

    [Fact]
    public async Task EnablePasswordRejectedReturnsClearError()
    {
        var factory = new FakeSessionFactory([
            "WGB>",
            "Password:",
            "Access denied\r\nWGB>"
        ]);
        var client = new SshNetWgbCommandClient(factory);

        var ex = await Assert.ThrowsAsync<WgbCommandException>(() => client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true, enablePassword: "wrong-secret"),
            CancellationToken.None));

        Assert.Contains("Enable password rejected", ex.Message);
    }

    [Fact]
    public async Task UnknownPromptReturnsClearError()
    {
        var factory = new FakeSessionFactory(["Welcome to WGB\r\n"]);
        var client = new SshNetWgbCommandClient(factory);

        var ex = await Assert.ThrowsAsync<WgbCommandException>(() => client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true),
            CancellationToken.None));

        Assert.Contains("prompt not detected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShellTimeoutPropagatesAsCommandTimeout()
    {
        var factory = new FakeSessionFactory([new TimeoutException("scripted timeout")]);
        var client = new SshNetWgbCommandClient(factory);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.ExecuteCommandAsync(
            CreateRequest(useEnableMode: true),
            CancellationToken.None));

        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WgbCommandRequest CreateRequest(
        bool useEnableMode,
        string enablePassword = "")
    {
        return new WgbCommandRequest(
            "192.0.2.10",
            22,
            "admin",
            "ssh-secret",
            "show wgb dot11 associations",
            TimeoutMilliseconds: 1000,
            useEnableMode,
            "enable",
            enablePassword);
    }

    private sealed class FakeSessionFactory : IWgbSshSessionFactory
    {
        private readonly Queue<object> _shellResponses;

        public FakeSessionFactory()
            : this([])
        {
        }

        public FakeSessionFactory(IEnumerable<object> shellResponses)
        {
            _shellResponses = new Queue<object>(shellResponses);
        }

        public string DirectCommandOutput { get; init; } = "";

        public FakeSession? Session { get; private set; }

        public IWgbSshSession Create(WgbCommandRequest request)
        {
            Session = new FakeSession(_shellResponses, DirectCommandOutput);
            return Session;
        }
    }

    private sealed class FakeSession : IWgbSshSession
    {
        private readonly Queue<object> _shellResponses;
        private readonly string _directCommandOutput;

        public FakeSession(
            Queue<object> shellResponses,
            string directCommandOutput)
        {
            _shellResponses = shellResponses;
            _directCommandOutput = directCommandOutput;
        }

        public bool IsConnected { get; private set; }

        public bool ShellCreated { get; private set; }

        public List<string> DirectCommands { get; } = [];

        public List<string> ShellLines { get; } = [];

        public void Connect(CancellationToken cancellationToken)
        {
            IsConnected = true;
        }

        public string ExecuteCommand(
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            DirectCommands.Add(command);
            return _directCommandOutput;
        }

        public IWgbSshShell CreateShell(CancellationToken cancellationToken)
        {
            ShellCreated = true;
            return new FakeShell(_shellResponses, ShellLines);
        }

        public void Disconnect()
        {
            IsConnected = false;
        }

        public void Dispose()
        {
            IsConnected = false;
        }
    }

    private sealed class FakeShell : IWgbSshShell
    {
        private readonly Queue<object> _responses;
        private readonly List<string> _lines;

        public FakeShell(
            Queue<object> responses,
            List<string> lines)
        {
            _responses = responses;
            _lines = lines;
        }

        public string Read(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return NextResponse();
        }

        public string SendLine(
            string line,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            _lines.Add(line);
            return NextResponse();
        }

        public void Dispose()
        {
        }

        private string NextResponse()
        {
            if (_responses.Count == 0)
            {
                return "";
            }

            var response = _responses.Dequeue();
            if (response is Exception ex)
            {
                throw ex;
            }

            return (string)response;
        }
    }
}
