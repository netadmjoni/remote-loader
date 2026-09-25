namespace WgbDiagnostics.Core.Wgb;

public interface IWgbSshSessionFactory
{
    IWgbSshSession Create(WgbCommandRequest request);
}

public interface IWgbSshSession : IDisposable
{
    bool IsConnected { get; }

    void Connect(CancellationToken cancellationToken);

    string ExecuteCommand(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    IWgbSshShell CreateShell(
        WgbSshShellOptions options,
        CancellationToken cancellationToken);

    void Disconnect();
}

public sealed record WgbSshShellOptions(
    bool RequestPseudoTerminal,
    string TerminalName,
    uint Columns,
    uint Rows,
    uint Width,
    uint Height,
    int BufferSize,
    bool UseMinimalTerminalModes)
{
    public static WgbSshShellOptions MinimalPty { get; } = new(
        RequestPseudoTerminal: true,
        TerminalName: "vt100",
        Columns: 120,
        Rows: 40,
        Width: 0,
        Height: 0,
        BufferSize: 4096,
        UseMinimalTerminalModes: true);

    public static WgbSshShellOptions NoPty { get; } = new(
        RequestPseudoTerminal: false,
        TerminalName: "",
        Columns: 0,
        Rows: 0,
        Width: 0,
        Height: 0,
        BufferSize: 4096,
        UseMinimalTerminalModes: true);
}

public interface IWgbSshShell : IDisposable
{
    string ReadAvailable(CancellationToken cancellationToken);

    void Write(
        string text,
        CancellationToken cancellationToken);

    void WriteLine(
        string line,
        CancellationToken cancellationToken);
}
