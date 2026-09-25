namespace WgbDiagnostics.Core.Wgb;

public sealed record WgbCommandExecutionResult(
    string RawOutput,
    WgbCommandExecutionDiagnostics Diagnostics);

public sealed record WgbCommandExecutionDiagnostics(
    bool ConnectionSucceeded,
    bool EnableAttempted,
    bool EnableSucceeded,
    bool CommandExecuted,
    bool FinalPromptConfirmed,
    bool PromptResyncAttempted,
    bool PromptResyncSucceeded,
    string? Warning,
    string? FailureReason,
    IReadOnlyList<WgbCommandDiagnosticEvent> Events);

public sealed record WgbCommandDiagnosticEvent(
    WgbPollEventKind Kind,
    DateTimeOffset Timestamp,
    string? Message);

public interface IWgbCommandDiagnosticsClient
{
    Task<WgbCommandExecutionResult> ExecuteCommandWithDiagnosticsAsync(
        WgbCommandRequest request,
        CancellationToken cancellationToken);
}

public interface IWgbPersistentCommandClient : IWgbCommandDiagnosticsClient
{
    Task ResetSessionAsync(CancellationToken cancellationToken);
}
