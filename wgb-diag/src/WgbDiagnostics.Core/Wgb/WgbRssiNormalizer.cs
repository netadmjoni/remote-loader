using System.Globalization;
using System.Text.RegularExpressions;

namespace WgbDiagnostics.Core.Wgb;

public sealed record WgbRssiNormalizationResult(string? Value, IReadOnlyList<string> Warnings);

public static class WgbRssiNormalizer
{
    private static readonly Regex RssiPattern = new(
        @"^\s*(?<sign>[+-]?)(?<number>\d+(?:[.,]\d+)?)\s*(?:dBm)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static WgbRssiNormalizationResult NormalizeForParserProfile(string? value, string parserProfile)
    {
        return Normalize(value, allowMagnitudeInput: WgbParserProfiles.Normalize(parserProfile) == WgbParserProfiles.Iw9167WgbV1);
    }

    public static string? NormalizeForDisplay(string? value)
    {
        return Normalize(value, allowMagnitudeInput: false).Value;
    }

    public static bool TryParseDbm(string? value, out double rssi)
    {
        rssi = 0;
        var normalized = NormalizeForDisplay(value);
        if (normalized is null)
        {
            return false;
        }

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out rssi);
    }

    private static WgbRssiNormalizationResult Normalize(string? value, bool allowMagnitudeInput)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new WgbRssiNormalizationResult(null, []);
        }

        var trimmed = value.Trim();
        var match = RssiPattern.Match(trimmed);
        if (!match.Success)
        {
            return new WgbRssiNormalizationResult(null, [$"RSSI value '{trimmed}' is not a numeric dBm value."]);
        }

        var sign = match.Groups["sign"].Value;
        var numberText = match.Groups["number"].Value.Replace(',', '.');
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var magnitude))
        {
            return new WgbRssiNormalizationResult(null, [$"RSSI value '{trimmed}' is not a numeric dBm value."]);
        }

        if (sign == "-")
        {
            var signed = -magnitude;
            var warning = IsPlausibleWifiRssi(signed)
                ? null
                : $"RSSI value '{trimmed}' is outside the expected Wi-Fi dBm range.";
            return new WgbRssiNormalizationResult(FormatDbmValue(signed), warning is null ? [] : [warning]);
        }

        if (sign == "+")
        {
            return new WgbRssiNormalizationResult(null, [$"RSSI value '{trimmed}' is positive dBm and was ignored."]);
        }

        if (allowMagnitudeInput && magnitude > 0 && magnitude <= 127)
        {
            // Some IW9167 association outputs and the reference script samples expose RSSI as a magnitude.
            var signed = -magnitude;
            return new WgbRssiNormalizationResult(
                FormatDbmValue(signed),
                [$"RSSI magnitude '{trimmed}' normalized to {FormatDbmValue(signed)} dBm for iw9167-wgb-v1."]);
        }

        return new WgbRssiNormalizationResult(null, [$"RSSI value '{trimmed}' has no negative sign and was ignored."]);
    }

    private static bool IsPlausibleWifiRssi(double value)
    {
        return value < 0 && value >= -127;
    }

    private static string FormatDbmValue(double value)
    {
        return value % 1 == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
