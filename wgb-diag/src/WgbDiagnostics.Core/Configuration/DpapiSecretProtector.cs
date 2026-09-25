using System.Security.Cryptography;
using System.Text;

namespace WgbDiagnostics.Core.Configuration;

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("WgbDiagnostics.v1");

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return "";
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI credential protection is only available on Windows.");
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, OptionalEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public SecretUnprotectResult TryUnprotect(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return SecretUnprotectResult.Success("");
        }

        if (!OperatingSystem.IsWindows())
        {
            return SecretUnprotectResult.Failure("Saved credential could not be decrypted: Windows DPAPI is only available on Windows.");
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedText);
            var bytes = ProtectedData.Unprotect(protectedBytes, OptionalEntropy, DataProtectionScope.CurrentUser);
            return SecretUnprotectResult.Success(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or PlatformNotSupportedException)
        {
            return SecretUnprotectResult.Failure($"Saved credential could not be decrypted: {ex.Message}");
        }
    }
}
