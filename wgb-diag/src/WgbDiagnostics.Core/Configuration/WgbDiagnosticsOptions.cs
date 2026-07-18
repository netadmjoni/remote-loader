namespace WgbDiagnostics.Core.Configuration;

public sealed class WgbDiagnosticsOptions
{
    public const string SectionName = "WgbDiagnostics";

    public string ApplicationName { get; set; } = "WGB Diagnostics";

    public string WgbAddress { get; set; } = "192.168.1.1";

    public int SshPort { get; set; } = 22;

    public string SshUsername { get; set; } = "root";

    public string EncryptedPasswordPlaceholder { get; set; } = "";

    public int WgbPollIntervalSeconds { get; set; } = 1;

    public string WgbCommand { get; set; } = "show wgb dot11 associations";

    public string ParserProfile { get; set; } = "iw9167-wgb-v1";

    public string PingTarget { get; set; } = "8.8.8.8";

    public int PingIntervalMilliseconds { get; set; } = 100;

    public int PingTimeoutMilliseconds { get; set; } = 1000;

    public int LossThresholdMilliseconds { get; set; } = 600;

    public bool RawLoggingEnabled { get; set; }

    public string LogDirectory { get; set; } = "Logs";

    public bool DailyRotationEnabled { get; set; } = true;

    public int RetentionDays { get; set; } = 14;

    public int GraphVisibleMinutes { get; set; } = 60;

    public bool WgbLogCollectionEnabled { get; set; }

    public int TftpTimeoutSeconds { get; set; } = 30;

    public long MaximumReceivedFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    public static WgbDiagnosticsOptions CreateDefault() => new();
}
