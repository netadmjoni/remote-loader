namespace WgbDiagnostics.Core.Wgb;

public enum WgbPollEventKind
{
    Connected,
    Disconnected,
    PollSucceeded,
    PollFailed,
    AssociationUpdated,
    ParentApChanged
}
