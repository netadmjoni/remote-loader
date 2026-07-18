namespace WgbDiagnostics.Core.Wgb;

public sealed record WgbCommandRequest(
    string Address,
    int Port,
    string Username,
    string Password,
    string Command,
    int TimeoutMilliseconds);
