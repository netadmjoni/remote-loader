namespace WgbDiagnostics.Core.Configuration;

public sealed class WgbDiagnosticsOptionsValidator : IConfigurationValidator<WgbDiagnosticsOptions>
{
    public IReadOnlyList<ConfigurationValidationError> Validate(WgbDiagnosticsOptions? options)
    {
        var errors = new List<ConfigurationValidationError>();

        if (options is null)
        {
            errors.Add(new ConfigurationValidationError("Configuration", "Configuration could not be loaded."));
            return errors;
        }

        AddRequiredTextError(errors, "Application name", options.ApplicationName);
        AddRequiredTextError(errors, "WGB address", options.WgbAddress);
        AddPortError(errors, "SSH port", options.SshPort);
        AddRequiredTextError(errors, "SSH username", options.SshUsername);
        AddEnableCommandError(errors, options.UseEnableMode, options.EnableCommand);
        AddPositiveIntegerError(errors, "WGB poll interval", options.WgbPollIntervalSeconds, "seconds");
        AddRequiredTextError(errors, "WGB command", options.WgbCommand);
        AddRequiredTextError(errors, "Parser profile", options.ParserProfile);
        AddRequiredTextError(errors, "Ping target", options.PingTarget);
        AddPingIntervalError(errors, options.PingIntervalMilliseconds);
        AddPositiveIntegerError(errors, "Ping timeout", options.PingTimeoutMilliseconds, "milliseconds");
        AddLossThresholdError(errors, options.LossThresholdMilliseconds, options.PingIntervalMilliseconds);
        AddRequiredTextError(errors, "Log directory", options.LogDirectory);
        AddPositiveIntegerError(errors, "Retention days", options.RetentionDays, "days");
        AddPositiveIntegerError(errors, "Graph visible minutes", options.GraphVisibleMinutes, "minutes");
        AddPositiveIntegerError(errors, "TFTP timeout", options.TftpTimeoutSeconds, "seconds");
        AddPositiveLongError(errors, "Maximum received file size", options.MaximumReceivedFileSizeBytes, "bytes");

        return errors;
    }

    private static void AddEnableCommandError(
        ICollection<ConfigurationValidationError> errors,
        bool useEnableMode,
        string? value)
    {
        if (useEnableMode)
        {
            AddRequiredTextError(errors, "Enable command", value);
            return;
        }

        if (!string.IsNullOrWhiteSpace(value) && value.Any(char.IsControl))
        {
            errors.Add(new ConfigurationValidationError("Enable command", "Enable command cannot contain control characters."));
        }
    }

    private static void AddRequiredTextError(
        ICollection<ConfigurationValidationError> errors,
        string field,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new ConfigurationValidationError(field, $"{field} is required."));
            return;
        }

        if (value.Any(char.IsControl))
        {
            errors.Add(new ConfigurationValidationError(field, $"{field} cannot contain control characters."));
        }
    }

    private static void AddPortError(
        ICollection<ConfigurationValidationError> errors,
        string field,
        int value)
    {
        if (value is < 1 or > 65535)
        {
            errors.Add(new ConfigurationValidationError(field, $"{field} must be between 1 and 65535."));
        }
    }

    private static void AddPositiveIntegerError(
        ICollection<ConfigurationValidationError> errors,
        string field,
        int value,
        string unit)
    {
        if (value < 1)
        {
            errors.Add(new ConfigurationValidationError(field, $"{field} must be at least 1 {unit}."));
        }
    }

    private static void AddPositiveLongError(
        ICollection<ConfigurationValidationError> errors,
        string field,
        long value,
        string unit)
    {
        if (value < 1)
        {
            errors.Add(new ConfigurationValidationError(field, $"{field} must be at least 1 {unit}."));
        }
    }

    private static void AddPingIntervalError(
        ICollection<ConfigurationValidationError> errors,
        int value)
    {
        if (value is < 10 or > 60000)
        {
            errors.Add(new ConfigurationValidationError("Ping interval", "Ping interval must be between 10 and 60000 milliseconds."));
        }
    }

    private static void AddLossThresholdError(
        ICollection<ConfigurationValidationError> errors,
        int lossThresholdMilliseconds,
        int pingIntervalMilliseconds)
    {
        if (lossThresholdMilliseconds < 1)
        {
            errors.Add(new ConfigurationValidationError("Loss threshold", "Loss threshold must be greater than 0 milliseconds."));
            return;
        }

        if (lossThresholdMilliseconds < pingIntervalMilliseconds)
        {
            errors.Add(new ConfigurationValidationError("Loss threshold", "Loss threshold must be at least as large as the ping interval."));
        }
    }
}
