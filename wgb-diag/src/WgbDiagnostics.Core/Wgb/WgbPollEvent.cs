namespace WgbDiagnostics.Core.Wgb;

public sealed record WgbPollEvent(
    WgbPollEventKind Kind,
    DateTimeOffset Timestamp,
    WgbAssociationSnapshot? Association,
    WgbAssociationParseResult? ParseResult,
    string? RawOutput,
    string? Message,
    string? OldParentApName = null,
    string? NewParentApName = null,
    string? OldParentBssid = null,
    string? NewParentBssid = null,
    string? OldChannel = null,
    string? NewChannel = null,
    string? OldRadioId = null,
    string? NewRadioId = null,
    WgbRoamClassification RoamClassification = WgbRoamClassification.Unknown,
    string? PotentialBugMatchId = null);
