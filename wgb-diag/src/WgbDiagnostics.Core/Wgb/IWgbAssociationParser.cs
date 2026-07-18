namespace WgbDiagnostics.Core.Wgb;

public interface IWgbAssociationParser
{
    WgbAssociationParseResult Parse(string? rawOutput, string parserProfile);
}
