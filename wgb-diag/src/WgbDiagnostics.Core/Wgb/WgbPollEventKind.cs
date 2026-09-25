namespace WgbDiagnostics.Core.Wgb;

public enum WgbPollEventKind
{
    Connecting,
    Connected,
    PromptDetected,
    EnableSucceeded,
    CommandStarted,
    CommandOutputReceived,
    CommandCompleted,
    CommandWarning,
    PromptResyncStarted,
    PromptResyncSucceeded,
    PromptResyncFailed,
    SessionLost,
    ReconnectScheduled,
    Disconnected,
    PollSucceeded,
    PollFailed,
    AssociationUpdated,
    ParentApChanged
}
