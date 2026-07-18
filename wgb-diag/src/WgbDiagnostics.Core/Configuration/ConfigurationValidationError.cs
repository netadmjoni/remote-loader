namespace WgbDiagnostics.Core.Configuration;

public sealed record ConfigurationValidationError(string Field, string Message);
