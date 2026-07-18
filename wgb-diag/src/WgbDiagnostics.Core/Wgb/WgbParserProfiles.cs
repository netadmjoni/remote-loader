namespace WgbDiagnostics.Core.Wgb;

public static class WgbParserProfiles
{
    public const string Iw9167WgbV1 = "iw9167-wgb-v1";
    public const string GenericKeyValue = "generic-key-value";

    public static string Normalize(string? parserProfile)
    {
        return string.IsNullOrWhiteSpace(parserProfile)
            ? Iw9167WgbV1
            : parserProfile.Trim().ToLowerInvariant();
    }
}
