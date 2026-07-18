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

    IWgbSshShell CreateShell(CancellationToken cancellationToken);

    void Disconnect();
}

public interface IWgbSshShell : IDisposable
{
    string Read(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    string SendLine(
        string line,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
