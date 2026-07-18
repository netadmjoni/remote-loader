using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Logging;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class DiagnosticSessionLoggerTests
{
    [Fact]
    public async Task StartSessionCreatesSessionFolderAndConfigSnapshot()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var clock = new FakeDiagnosticClock(new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero));
        var logger = new DiagnosticSessionLogger(clock);

        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path, deviceOrTarget: "wgb/one"),
            CreateConfigSnapshot(),
            CancellationToken.None);
        await logger.StopSessionAsync(CancellationToken.None);

        Assert.True(Directory.Exists(session.SessionDirectory));
        Assert.Contains("wgb_one_20260718_100000", session.SessionDirectory);
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "config-snapshot.json")));
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "session-summary.json")));
    }

    [Fact]
    public async Task PingCsvContainsHeader()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path),
            CreateConfigSnapshot(),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.LossStarted, sequence: 2));
        await logger.StopSessionAsync(CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(session.SessionDirectory, "ping-events.csv"));
        Assert.Equal("timestamp,event,sequence,rtt_ms,consecutive_loss,loss_window_ms,message", lines[0]);
    }

    [Fact]
    public async Task PingLoggingIsEventOnlyWhenRawLoggingIsDisabled()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path, rawLoggingEnabled: false),
            CreateConfigSnapshot(),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, rttMilliseconds: 8));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.LossStarted, sequence: 2, consecutiveLoss: 1, lossWindow: 100));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.Loss, sequence: 3, consecutiveLoss: 2, lossWindow: 200));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.AlertThresholdReached, sequence: 3, consecutiveLoss: 2, lossWindow: 600));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.Recovered, sequence: 4, rttMilliseconds: 11));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.Error, sequence: 5, consecutiveLoss: 1, lossWindow: 10, message: "probe failed"));
        await logger.StopSessionAsync(CancellationToken.None);

        var rows = File.ReadAllLines(Path.Combine(session.SessionDirectory, "ping-events.csv"))
            .Skip(1)
            .Select(line => line.Split(',')[1])
            .ToArray();

        Assert.Equal(new[] { "LAST_OK", "LOSS_START", "ALERT", "RECOVER", "ERROR" }, rows);
        Assert.False(File.Exists(Path.Combine(session.SessionDirectory, "raw-ping.log")));
    }

    [Fact]
    public async Task RawLoggingWritesEveryPingProbeEventWhenEnabled()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path, rawLoggingEnabled: true),
            CreateConfigSnapshot(),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, rttMilliseconds: 4));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.LossStarted, sequence: 2));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.Loss, sequence: 3));
        await logger.StopSessionAsync(CancellationToken.None);

        var rawLog = File.ReadAllText(Path.Combine(session.SessionDirectory, "raw-ping.log"));
        Assert.Contains("PingReply", rawLog);
        Assert.Contains("LossStarted", rawLog);
        Assert.Contains("Loss", rawLog);
    }

    [Fact]
    public async Task DailyRotationCreatesDatedFilesAfterDateChanges()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var clock = new FakeDiagnosticClock(new DateTimeOffset(2026, 7, 18, 23, 59, 0, TimeSpan.Zero));
        var logger = new DiagnosticSessionLogger(clock);
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path, dailyRotationEnabled: true),
            CreateConfigSnapshot(),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.LossStarted, sequence: 1));
        clock.UtcNow = new DateTimeOffset(2026, 7, 19, 0, 0, 1, TimeSpan.Zero);
        await logger.LogPingEventAsync(Ping(
            IcmpMonitorEventKind.AlertThresholdReached,
            sequence: 2,
            timestamp: new DateTimeOffset(2026, 7, 19, 0, 0, 1, TimeSpan.Zero)));
        await logger.StopSessionAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "ping-events.csv")));
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "ping-events_20260719.csv")));
    }

    [Fact]
    public async Task StopSessionFlushesQueuedEventsAndSummary()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path),
            CreateConfigSnapshot(),
            CancellationToken.None);

        for (var sequence = 1; sequence <= 100; sequence++)
        {
            await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.LossStarted, sequence));
        }

        await logger.StopSessionAsync(CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(session.SessionDirectory, "ping-events.csv"));
        Assert.Equal(101, lines.Length);
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "session-summary.json")));
    }

    [Fact]
    public async Task CredentialsAreNotWrittenToSnapshotOrLogs()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        const string password = "super-secret-password";
        const string username = "admin-user";
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(
                testDirectory.Path,
                rawLoggingEnabled: true,
                sensitiveValues: [password, username]),
            CreateConfigSnapshot(username, password),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.Error, sequence: 1, message: $"failure {password}"));
        await logger.LogWgbEventAsync(new WgbPollEvent(
            WgbPollEventKind.PollSucceeded,
            DateTimeOffset.UtcNow,
            WgbAssociationSnapshot.Unknown,
            ParseResult: null,
            RawOutput: $"raw output {username} {password}",
            Message: $"ok {username}"));
        await logger.StopSessionAsync(CancellationToken.None);

        var allText = string.Join(
            Environment.NewLine,
            Directory.GetFiles(session.SessionDirectory)
                .Select(File.ReadAllText));

        Assert.DoesNotContain(password, allText);
        Assert.DoesNotContain(username, allText);
        Assert.Contains("[redacted]", allText);
    }

    private static DiagnosticSessionLoggerOptions CreateLoggerOptions(
        string logDirectory,
        string deviceOrTarget = "wgb-1",
        bool rawLoggingEnabled = false,
        bool dailyRotationEnabled = true,
        IReadOnlyList<string>? sensitiveValues = null)
    {
        return new DiagnosticSessionLoggerOptions(
            logDirectory,
            deviceOrTarget,
            rawLoggingEnabled,
            dailyRotationEnabled,
            RetentionDays: 14,
            sensitiveValues ?? []);
    }

    private static WgbDiagnosticsOptions CreateConfigSnapshot(
        string username = "root",
        string passwordPlaceholder = "")
    {
        return new WgbDiagnosticsOptions
        {
            SshUsername = username,
            EncryptedPasswordPlaceholder = passwordPlaceholder
        };
    }

    private static IcmpMonitorEvent Ping(
        IcmpMonitorEventKind kind,
        long sequence,
        int rttMilliseconds = 0,
        int consecutiveLoss = 0,
        int lossWindow = 0,
        string? message = null,
        DateTimeOffset? timestamp = null)
    {
        return new IcmpMonitorEvent(
            kind,
            timestamp ?? new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero).AddMilliseconds(sequence),
            sequence,
            rttMilliseconds > 0 ? TimeSpan.FromMilliseconds(rttMilliseconds) : null,
            consecutiveLoss,
            lossWindow,
            message);
    }

    private sealed class FakeDiagnosticClock : IDiagnosticClock
    {
        public FakeDiagnosticClock()
            : this(new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero))
        {
        }

        public FakeDiagnosticClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class TempDiagnosticDirectory : IAsyncDisposable
    {
        private TempDiagnosticDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDiagnosticDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "wgb-diagnostics-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDiagnosticDirectory(path);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
