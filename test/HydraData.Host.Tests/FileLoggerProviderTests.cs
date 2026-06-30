// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HydraData.Host.Tests;

/// <summary>
/// T07.4 — unit tests for <see cref="FileLoggerProvider"/>, the host's minimal "Console + Datei" file sink:
/// it creates the log directory if missing, writes one structured line per entry (ISO timestamp, level,
/// category, message), honours the minimum-level filter, and is a silent no-op after <see cref="IDisposable.Dispose"/>.
/// The final test is a security guard: a connection secret (a fake password) parsed/resolved by
/// <see cref="ConnectionRegistry"/> must never reach the log file, even when the parse/resolve fails and the
/// exception is logged at Error.
/// </summary>
public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir;

    public FileLoggerProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hydradata-filelogger", Path.GetRandomFileName());
        // Deliberately NOT created here: the "creates the directory" test asserts the provider creates it.
    }

    private string LogPath(string name = "host.log") => Path.Combine(_dir, name);

    // ── Directory creation ────────────────────────────────────────────────────────

    [Fact]
    public void Creates_the_log_directory_if_missing()
    {
        Assert.False(Directory.Exists(_dir)); // precondition

        using var provider = new FileLoggerProvider(LogPath());

        Assert.True(Directory.Exists(_dir), "the provider must create its log directory eagerly.");
    }

    // ── Line content: level + category + message + ISO timestamp ──────────────────

    [Fact]
    public void Written_line_contains_level_category_message_and_iso_timestamp()
    {
        using (var provider = new FileLoggerProvider(LogPath()))
        {
            var logger = provider.CreateLogger("My.Category");
            logger.LogInformation("hello-world-message");
        }

        var text = File.ReadAllText(LogPath());

        Assert.Contains("Information", text, StringComparison.Ordinal); // level
        Assert.Contains("My.Category", text, StringComparison.Ordinal); // category
        Assert.Contains("hello-world-message", text, StringComparison.Ordinal); // message
        // ISO-8601 round-trip timestamp prefix (yyyy-MM-ddTHH:mm:ss…). Assert the date/T-separator shape.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", text);
    }

    // ── Minimum-level filter ──────────────────────────────────────────────────────

    [Fact]
    public void Min_level_filter_suppresses_below_threshold_entries()
    {
        using (var provider = new FileLoggerProvider(LogPath(), LogLevel.Warning))
        {
            var logger = provider.CreateLogger("Cat");
            logger.LogInformation("below-threshold-info"); // < Warning → suppressed
            logger.LogDebug("below-threshold-debug");      // < Warning → suppressed
            logger.LogWarning("at-threshold-warning");     // == Warning → written
            logger.LogError("above-threshold-error");      // > Warning → written
        }

        var text = File.ReadAllText(LogPath());

        Assert.DoesNotContain("below-threshold-info", text, StringComparison.Ordinal);
        Assert.DoesNotContain("below-threshold-debug", text, StringComparison.Ordinal);
        Assert.Contains("at-threshold-warning", text, StringComparison.Ordinal);
        Assert.Contains("above-threshold-error", text, StringComparison.Ordinal);
    }

    // ── Write-after-Dispose is a silent no-op ─────────────────────────────────────

    [Fact]
    public void Write_after_dispose_is_a_silent_no_op()
    {
        var provider = new FileLoggerProvider(LogPath());
        var logger = provider.CreateLogger("Cat");
        logger.LogInformation("before-dispose");

        provider.Dispose();

        // Logging after Dispose must neither throw nor append anything.
        var ex = Record.Exception(() => logger.LogInformation("after-dispose"));
        Assert.Null(ex);

        var text = File.ReadAllText(LogPath());
        Assert.Contains("before-dispose", text, StringComparison.Ordinal);
        Assert.DoesNotContain("after-dispose", text, StringComparison.Ordinal);
    }

    // ── Security: a connection secret must NOT reach the log file ─────────────────

    [Fact]
    public void Connection_secret_never_reaches_the_log_file_even_when_parse_fails_and_is_logged()
    {
        // A connections.xml carrying a real-looking Password secret PLUS a triggering parse error (a
        // <ConnectionString> missing its required targetSystem attribute). ConnectionRegistry.Parse must
        // throw a FormatException whose message names only the element/missing attribute — never the
        // Password value. Routing that exception through a FileLoggerProvider (the same sink the host uses)
        // and reading the file back proves no secret leaks into host.log. If "s3cr3t" appears here, a secret
        // leaked into the log — a real security bug.
        const string secret = "s3cr3t-PaSSw0rd";
        var xml = $"""
            <ConnectionStrings>
              <ConnectionString name="stage">
                <Parameters>
                  <Parameter key="Server"   value="localhost" type="String" />
                  <Parameter key="Password" value="{secret}"   type="String" />
                </Parameters>
              </ConnectionString>
            </ConnectionStrings>
            """;

        using (var provider = new FileLoggerProvider(LogPath()))
        {
            var logger = provider.CreateLogger("HydraData.Host");

            var ex = Assert.Throws<FormatException>(() => ConnectionRegistry.Parse(xml));

            // Sanity: the secret must not even be in the exception message that the host would log.
            Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);

            // Log the exception the way HostExit.MapConfigExceptionAsync does (message + full exception).
            logger.LogError(ex, "Configuration/preflight failure; the run did not start.");
        }

        var text = File.ReadAllText(LogPath());
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
