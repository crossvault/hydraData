// Copyright (c) 2026 crossVault GmbH.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HydraData.Host.Tests;

/// <summary>
/// Host-side run-directory retention: deletes <c>RunId</c> folders older
/// than a cutoff and keeps newer ones. The engine enforces no retention; this is a host convention. The
/// cutoff is injected so the test never touches the wall clock.
/// </summary>
public class RunDirRetentionTests : IDisposable
{
    private readonly string _workspace;
    private readonly RunDirRetention _retention = new(NullLogger.Instance);

    public RunDirRetentionTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "HydraData-host-retention", Path.GetRandomFileName());
        Directory.CreateDirectory(_workspace);
    }

    private string MakeRunDir(string name, DateTimeOffset lastWriteUtc)
    {
        var dir = Path.Combine(_workspace, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        Directory.SetLastWriteTimeUtc(dir, lastWriteUtc.UtcDateTime);
        return dir;
    }

    private static string NewRunId() => Guid.NewGuid().ToString("D");

    [Fact]
    public void Deletes_only_folders_older_than_cutoff()
    {
        var cutoff = new DateTimeOffset(2026, 06, 24, 0, 0, 0, TimeSpan.Zero);
        var old = MakeRunDir(NewRunId(), cutoff.AddDays(-5));
        var fresh = MakeRunDir(NewRunId(), cutoff.AddDays(+1));

        var deleted = _retention.CleanOlderThan(_workspace, cutoff);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void Keeps_folder_exactly_at_cutoff()
    {
        var cutoff = new DateTimeOffset(2026, 06, 24, 0, 0, 0, TimeSpan.Zero);
        var atCutoff = MakeRunDir(NewRunId(), cutoff);

        var deleted = _retention.CleanOlderThan(_workspace, cutoff);

        Assert.Equal(0, deleted);
        Assert.True(Directory.Exists(atCutoff));
    }

    [Fact]
    public void Missing_workspace_base_is_a_no_op()
    {
        var deleted = _retention.CleanOlderThan(Path.Combine(_workspace, "does-not-exist"), DateTimeOffset.UtcNow);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public void CleanOlderThanDays_uses_injected_clock()
    {
        var now = new DateTimeOffset(2026, 06, 24, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var old = MakeRunDir(NewRunId(), now.AddDays(-20));
        var recent = MakeRunDir(NewRunId(), now.AddDays(-2));

        var deleted = _retention.CleanOlderThanDays(_workspace, retentionDays: 14, timeProvider: clock);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void Zero_retention_days_disables_cleanup()
    {
        var old = MakeRunDir(NewRunId(), DateTimeOffset.UtcNow.AddDays(-100));

        var deleted = _retention.CleanOlderThanDays(_workspace, retentionDays: 0);

        Assert.Equal(0, deleted);
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public void Old_non_guid_directory_survives_and_is_not_counted()
    {
        var cutoff = new DateTimeOffset(2026, 06, 24, 0, 0, 0, TimeSpan.Zero);
        var nonRunDirectory = MakeRunDir("scripts", cutoff.AddDays(-30));

        var deleted = _retention.CleanOlderThan(_workspace, cutoff);

        Assert.Equal(0, deleted);
        Assert.True(Directory.Exists(nonRunDirectory));
    }

    [Fact]
    public void IOException_from_delete_is_skipped_and_other_eligible_run_is_deleted()
    {
        var cutoff = new DateTimeOffset(2026, 06, 24, 0, 0, 0, TimeSpan.Zero);
        var blocked = MakeRunDir(NewRunId(), cutoff.AddDays(-5));
        var deletable = MakeRunDir(NewRunId(), cutoff.AddDays(-6));
        var fresh = MakeRunDir(NewRunId(), cutoff.AddDays(+1));
        var attempted = new List<string>();
        var retention = new RunDirRetention(
            NullLogger.Instance,
            (path, recursive) =>
            {
                Assert.True(recursive);
                attempted.Add(path);
                if (path == blocked)
                    throw new IOException("deterministic delete failure");

                Directory.Delete(path, recursive);
            });

        var deleted = retention.CleanOlderThan(_workspace, cutoff);

        Assert.Equal(1, deleted);
        Assert.True(Directory.Exists(blocked));
        Assert.False(Directory.Exists(deletable));
        Assert.True(Directory.Exists(fresh));
        Assert.Contains(blocked, attempted);
        Assert.Contains(deletable, attempted);
        Assert.DoesNotContain(fresh, attempted);
    }

    [Fact]
    public void Locked_run_directory_is_skipped_best_effort_and_others_are_deleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileShare.None blocks deletion only on Windows.");

        // A run directory holding an open file with FileShare.None cannot be deleted (Windows). Retention
        // must NOT throw on it: it logs and moves on, leaving the locked dir intact and uncounted, while the
        // other old directories ARE deleted. Proves the IOException/UnauthorizedAccessException best-effort
        // guard in CleanOlderThan.
        var cutoff = new DateTimeOffset(2026, 06, 24, 0, 0, 0, TimeSpan.Zero);

        var lockedLastWrite = cutoff.AddDays(-5);
        var lockedDir = MakeRunDir(NewRunId(), lockedLastWrite);
        var otherOld1 = MakeRunDir(NewRunId(), cutoff.AddDays(-6));
        var otherOld2 = MakeRunDir(NewRunId(), cutoff.AddDays(-7));
        var fresh = MakeRunDir(NewRunId(), cutoff.AddDays(+1));

        // Hold a file open with NO sharing so Directory.Delete(recursive) on the locked dir fails.
        var lockPath = Path.Combine(lockedDir, "held.lock");
        using (var held = new FileStream(lockPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // Creating the lock file refreshes the directory mtime. Restore the old timestamp so retention
            // reaches Directory.Delete and exercises its best-effort IOException handling.
            Directory.SetLastWriteTimeUtc(lockedDir, lockedLastWrite.UtcDateTime);

            var deleted = _retention.CleanOlderThan(_workspace, cutoff);

            // Only the two unlocked old dirs were deletable; the locked one is skipped (not counted).
            Assert.Equal(2, deleted);
            Assert.True(Directory.Exists(lockedDir), "the locked run dir must survive the retention pass.");
            Assert.False(Directory.Exists(otherOld1));
            Assert.False(Directory.Exists(otherOld2));
            Assert.True(Directory.Exists(fresh), "newer dirs are never touched.");
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>A trivial fixed-time <see cref="TimeProvider"/> for deterministic retention tests.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
