// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Read-/write-allowlist for the file sandbox. Both lists hold absolute,
/// normalised folder paths. A read path must lie under at least one <see cref="ReadAllowlist"/> folder
/// (or under the run's <c>RunDir</c>, which is implicit); a write path under at least one
/// <see cref="WriteAllowlist"/> folder (or <c>RunDir</c>). The policy is a guardrail for trusted
/// scripts, not an OS jail; there is no symlink resolution.
/// </summary>
/// <param name="ReadAllowlist">Absolute, normalised folders scripts may read from.</param>
/// <param name="WriteAllowlist">Absolute, normalised folders scripts may write to.</param>
public sealed record PumpFolderPolicy(
    IReadOnlyList<string> ReadAllowlist,
    IReadOnlyList<string> WriteAllowlist)
{
    /// <summary>An empty policy (only the run's <c>RunDir</c> is readable/writable).</summary>
    public static PumpFolderPolicy Empty { get; } = new([], []);
}
