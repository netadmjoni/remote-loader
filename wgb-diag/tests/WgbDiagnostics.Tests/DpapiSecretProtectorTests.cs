using System.Text.Json;
using WgbDiagnostics.Core.Configuration;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class DpapiSecretProtectorTests
{
    [Fact]
    public void ProtectAndUnprotectRoundTripsWithCurrentUserScope()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiSecretProtector();

        var protectedText = protector.Protect("ssh-secret");
        var result = protector.TryUnprotect(protectedText);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotEqual("ssh-secret", protectedText);
        Assert.Equal("ssh-secret", result.Plaintext);
    }

    [Fact]
    public void CorruptProtectedValueReturnsFailureWithoutThrowing()
    {
        var protector = new DpapiSecretProtector();

        var result = protector.TryUnprotect("not valid base64");

        Assert.False(result.Succeeded);
        Assert.Contains("could not be decrypted", result.ErrorMessage);
    }

    [Fact]
    public void SerializedConfigDoesNotContainPlaintextSecret()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiSecretProtector();
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.SaveSshPassword = true;
        options.EncryptedPasswordPlaceholder = protector.Protect("ssh-secret");
        options.SaveEnablePassword = true;
        options.EncryptedEnablePasswordPlaceholder = protector.Protect("enable-secret");

        var json = JsonSerializer.Serialize(options);

        Assert.DoesNotContain("ssh-secret", json);
        Assert.DoesNotContain("enable-secret", json);
        Assert.Contains("\"EncryptedPasswordPlaceholder\":", json);
        Assert.Contains("\"EncryptedEnablePasswordPlaceholder\":", json);
        Assert.NotEmpty(options.EncryptedPasswordPlaceholder);
        Assert.NotEmpty(options.EncryptedEnablePasswordPlaceholder);
    }

    [Fact]
    public void ClearingSavedSecretsRemovesProtectedValuesFromConfig()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.SaveSshPassword = false;
        options.EncryptedPasswordPlaceholder = "";
        options.SaveEnablePassword = false;
        options.EncryptedEnablePasswordPlaceholder = "";

        var json = JsonSerializer.Serialize(options);

        Assert.Contains("\"SaveSshPassword\":false", json);
        Assert.Contains("\"SaveEnablePassword\":false", json);
        Assert.Contains("\"EncryptedPasswordPlaceholder\":\"\"", json);
        Assert.Contains("\"EncryptedEnablePasswordPlaceholder\":\"\"", json);
    }
}
