namespace WgbDiagnostics.Core.Wgb;

public static class WgbParserProfiles
{
    public const string Iw9167WgbV1 = "iw9167-wgb-v1";
    public const string GenericKeyValue = "generic-key-value";
    public const string Iw9167WgbV1PollingCommand = "show wgb dot11 associations";

    public static string Normalize(string? parserProfile)
    {
        return string.IsNullOrWhiteSpace(parserProfile)
            ? Iw9167WgbV1
            : parserProfile.Trim().ToLowerInvariant();
    }

    public static string ResolvePollingCommand(string? parserProfile, string? configuredCommand)
    {
        if (Normalize(parserProfile) == Iw9167WgbV1)
        {
            return Iw9167WgbV1PollingCommand;
        }

        return string.IsNullOrWhiteSpace(configuredCommand)
            ? Iw9167WgbV1PollingCommand
            : configuredCommand.Trim();
    }
}
