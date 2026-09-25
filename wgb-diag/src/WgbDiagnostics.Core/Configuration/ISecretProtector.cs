namespace WgbDiagnostics.Core.Configuration;

public interface ISecretProtector
{
    string Protect(string plaintext);

    SecretUnprotectResult TryUnprotect(string protectedText);
}

public sealed record SecretUnprotectResult(
    bool Succeeded,
    string Plaintext,
    string? ErrorMessage)
{
    public static SecretUnprotectResult Success(string plaintext)
    {
        return new SecretUnprotectResult(true, plaintext, ErrorMessage: null);
    }

    public static SecretUnprotectResult Failure(string errorMessage)
    {
        return new SecretUnprotectResult(false, Plaintext: "", errorMessage);
    }
}
