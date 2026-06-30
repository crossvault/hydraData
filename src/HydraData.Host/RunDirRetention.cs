// Copyright (c) 2026 crossVault GmbH.

using Microsoft.Extensions.Logging;

namespace HydraData.Host;

/// <summary>
/// Host-side retention for run directories: deletes <c>RunId</c>
/// folders directly under the workspace base whose last-write time is older than a cutoff. The engine
/// deliberately enforces no retention; this is purely a host convention.
/// </summary>
/// <remarks>
/// The cutoff is passed in (or derived from an injected <see cref="TimeProvider"/>) so the routine is fully
/// testable without touching the wall clock. Only immediate child directories of the workspace base are
/// considered, matching the engine's layout <c>WorkspaceBase/&lt;RunId&gt;</c>.
/// </remarks>
public sealed class RunDirRetention
{
    private readonly ILogger _logger;

    /// <summary>Creates a retention cleaner.</summary>
    /// <param name="logger">Diagnostic logger.</param>
    public RunDirRetention(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Deletes every run directory directly under <paramref name="workspaceBase"/> last written before
    /// <paramref name="cutoff"/>. Newer folders are kept. Missing workspace base is a no-op.
    /// </summary>
    /// <param name="workspaceBase">The base directory whose child run folders are cleaned.</param>
    /// <param name="cutoff">Folders last written strictly before this instant are deleted.</param>
    /// <returns>The number of run directories deleted.</returns>
    /// <exception cref="ArgumentException"><paramref name="workspaceBase"/> is null/empty/whitespace.</exception>
    public int CleanOlderThan(string workspaceBase, DateTimeOffset cutoff)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceBase);

        if (!Directory.Exists(workspaceBase))
        {
            _logger.LogDebug("Retention: workspace base '{WorkspaceBase}' does not exist; nothing to clean.", workspaceBase);
            return 0;
        }

        var deleted = 0;
        foreach (var dir in Directory.EnumerateDirectories(workspaceBase))
        {
            // Use the last write time of the run folder itself; a run writes into it during execution, so
            // its mtime tracks the run's recency without parsing the RunId.
            var lastWrite = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero);
            if (lastWrite >= cutoff)
                continue;

            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
                _logger.LogInformation("Retention: deleted run directory '{RunDir}' (last write {LastWrite:o}).", dir, lastWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked/in-use run folder must not abort the host; log and move on.
                _logger.LogWarning(ex, "Retention: could not delete run directory '{RunDir}'.", dir);
            }
        }

        return deleted;
    }

    /// <summary>
    /// Convenience overload deriving the cutoff from an injected <see cref="TimeProvider"/> and a retention
    /// window in days. A non-positive <paramref name="retentionDays"/> disables retention (no-op).
    /// </summary>
    /// <param name="workspaceBase">The base directory whose child run folders are cleaned.</param>
    /// <param name="retentionDays">Folders older than this many days are deleted; <c>&lt;= 0</c> disables.</param>
    /// <param name="timeProvider">Clock used to compute "now"; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <returns>The number of run directories deleted.</returns>
    public int CleanOlderThanDays(string workspaceBase, int retentionDays, TimeProvider? timeProvider = null)
    {
        if (retentionDays <= 0)
        {
            _logger.LogDebug("Retention disabled (RunDirRetentionDays = {Days}).", retentionDays);
            return 0;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return CleanOlderThan(workspaceBase, now.AddDays(-retentionDays));
    }
}
