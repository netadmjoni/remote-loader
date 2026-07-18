namespace WgbDiagnostics.Core.Wgb;

public sealed record WgbAssociationParseResult(
    WgbAssociationSnapshot Association,
    string ParserProfile,
    IReadOnlyList<string> MatchedFields,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> UnclassifiedLines);
