// Copyright (c) 2026 crossVault GmbH.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HydraData.Engine;

/// <summary>
/// The per-run filesystem sandbox. Each run owns a
/// <see cref="RunDir"/> = <c>WorkspaceBase/&lt;RunId&gt;</c> with fixed, lazily created subdirectories
/// (<see cref="Out"/>, <see cref="Duck"/>, <see cref="Tmp"/>). <see cref="ResolveRead"/> and
/// <see cref="ResolveWrite"/> normalise a path to an absolute path and enforce the
/// <see cref="PumpFolderPolicy"/> allowlists; <see cref="RunDir"/> is implicitly readable and writable.
/// </summary>
/// <remarks>
/// This is a guardrail for trusted scripts, not an OS jail. <see cref="IsInside"/> compares normalised
/// absolute paths with a separator-prefix guard so that, for example, <c>/a/bc</c> is not treated as
/// inside <c>/a/b</c>. There is no symlink resolution: a symlink that
/// lives inside an allowed folder but points outside is judged by its lexical path, so it is reported as
/// inside the allowlist. Untrusted, symlink-bearing inputs are out of scope for this layer.
/// </remarks>
public sealed class Workspace
{
    private readonly string _workspaceBase;
    private readonly PumpFolderPolicy _policy;
    private readonly IReadOnlyList<string> _readRoots;
    private readonly IReadOnlyList<string> _writeRoots;
    private readonly ILogger _logger;

    /// <summary>Initializes a run sandbox.</summary>
    /// <param name="workspaceBase">The base directory under which run directories are created.</param>
    /// <param name="runId">The run identifier (generated via <see cref="IGuidProvider"/> before validate).</param>
    /// <param name="policy">The read-/write-allowlist policy. Defaults to <see cref="PumpFolderPolicy.Empty"/>.</param>
    /// <param name="logger">Diagnostic logger (Debug for the resolved run directory). Defaults to <see cref="NullLogger.Instance"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="workspaceBase"/> is null or whitespace.</exception>
    public Workspace(string workspaceBase, Guid runId, PumpFolderPolicy? policy = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceBase);

        _workspaceBase = NormalizeFull(workspaceBase);
        RunId = runId;
        RunDir = NormalizeFull(Path.Combine(_workspaceBase, runId.ToString("D")));
        _policy = policy ?? PumpFolderPolicy.Empty;
        _logger = logger ?? NullLogger.Instance;

        // Normalise allowlist roots once; RunDir is implicitly in both.
        _readRoots = BuildRoots(_policy.ReadAllowlist, RunDir);
        _writeRoots = BuildRoots(_policy.WriteAllowlist, RunDir);

        _logger.LogDebug("Workspace run directory resolved to {RunDir}.", RunDir);
    }

    /// <summary>The run identifier.</summary>
    public Guid RunId { get; }

    /// <summary>The run directory (<c>WorkspaceBase/&lt;RunId&gt;</c>), normalised and absolute.</summary>
    public string RunDir { get; }

    /// <summary>The <c>out/</c> subdirectory for deliberate script outputs; created lazily on access.</summary>
    public string Out => EnsureSubdir("out");

    /// <summary>The <c>duck/</c> subdirectory for non-memory DuckDB files; created lazily on access.</summary>
    public string Duck => EnsureSubdir("duck");

    /// <summary>The <c>tmp/</c> subdirectory for throwaway intermediates; created lazily on access.</summary>
    public string Tmp => EnsureSubdir("tmp");

    /// <summary>
    /// Resolves a read path to an absolute path and verifies it lies under the read allowlist or
    /// <see cref="RunDir"/>.
    /// </summary>
    /// <param name="path">A relative or absolute path. Relative paths resolve against <see cref="RunDir"/>.</param>
    /// <returns>The normalised, absolute path.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or whitespace.</exception>
    /// <exception cref="UnauthorizedAccessException">The path is outside the read allowlist and <see cref="RunDir"/>.</exception>
    public string ResolveRead(string path) => Resolve(path, _readRoots, "read", _policy.ReadAllowlist.Count);

    /// <summary>
    /// Resolves a write path to an absolute path and verifies it lies under the write allowlist or
    /// <see cref="RunDir"/>.
    /// </summary>
    /// <param name="path">A relative or absolute path. Relative paths resolve against <see cref="RunDir"/>.</param>
    /// <returns>The normalised, absolute path.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or whitespace.</exception>
    /// <exception cref="UnauthorizedAccessException">The path is outside the write allowlist and <see cref="RunDir"/>.</exception>
    public string ResolveWrite(string path) => Resolve(path, _writeRoots, "write", _policy.WriteAllowlist.Count);

    /// <summary>
    /// Returns whether <paramref name="candidate"/> is the same as, or nested inside, <paramref name="root"/>.
    /// Both are compared as normalised absolute paths; a separator-prefix guard prevents a sibling such as
    /// <c>/a/bc</c> from being treated as inside <c>/a/b</c>. No symlink resolution is performed.
    /// </summary>
    /// <param name="root">The candidate parent directory.</param>
    /// <param name="candidate">The path to test.</param>
    /// <returns><see langword="true"/> if <paramref name="candidate"/> is at or under <paramref name="root"/>.</returns>
    public static bool IsInside(string root, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        var normalizedRoot = TrimTrailingSeparators(NormalizeFull(root));
        var normalizedCandidate = TrimTrailingSeparators(NormalizeFull(candidate));

        // Paths are case-insensitive on Windows, case-sensitive elsewhere; honour the platform.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(normalizedRoot, normalizedCandidate, comparison))
            return true;

        // Separator-prefix guard: the candidate must start with "<root><separator>" so that a sibling
        // directory sharing a textual prefix (e.g. "/a/bc" vs "/a/b") is rejected.
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, comparison);
    }

    private string EnsureSubdir(string name)
    {
        var dir = Path.Combine(RunDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string Resolve(string path, IReadOnlyList<string> roots, string mode, int allowlistCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Relative paths resolve against RunDir so a script can use plain names inside its own run.
        var full = Path.IsPathRooted(path)
            ? NormalizeFull(path)
            : NormalizeFull(Path.Combine(RunDir, path));

        foreach (var root in roots)
        {
            if (IsInside(root, full))
                return full;
        }

        // Clear, non-silent failure.
        var hint = allowlistCount == 0
            ? $" The {mode} allowlist is empty, so only the run directory is accessible; add the input folder to the {mode} allowlist."
            : string.Empty;

        throw new UnauthorizedAccessException(
            $"Path '{full}' is outside the {mode} sandbox (allowlist folders and the run directory '{RunDir}')." + hint);
    }

    private static IReadOnlyList<string> BuildRoots(IReadOnlyList<string> allowlist, string runDir)
    {
        var roots = new List<string>(allowlist.Count + 1) { runDir };
        foreach (var folder in allowlist)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(folder, nameof(allowlist));

            // Allowlist entries must be absolute (PumpFolderPolicy docs say "absolute, normalised
            // folders"). A relative entry would resolve against the process CWD — an unstable, surprising
            // root that could silently widen the sandbox — so reject it.
            if (!Path.IsPathRooted(folder))
            {
                throw new ArgumentException(
                    $"Allowlist folder '{folder}' must be an absolute path.", nameof(allowlist));
            }

            roots.Add(NormalizeFull(folder));
        }

        return roots;
    }

    private static string NormalizeFull(string path) =>
        TrimTrailingSeparators(Path.GetFullPath(path));

    private static string TrimTrailingSeparators(string path)
    {
        // Keep a trailing separator only for a root such as "C:\" or "/".
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }
}
