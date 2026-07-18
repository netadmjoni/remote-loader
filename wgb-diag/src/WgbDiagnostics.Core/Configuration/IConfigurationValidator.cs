namespace WgbDiagnostics.Core.Configuration;

public interface IConfigurationValidator<in TOptions>
{
    IReadOnlyList<ConfigurationValidationError> Validate(TOptions? options);
}
