// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// The immutable result of a <see cref="DiscoveryService.Discover"/> call.
/// Carries all discovered steps, groups, and loader warnings and serves as
/// the input to <c>Validate</c> and <c>Execute</c> operations.
/// </summary>
public sealed class ScriptContext
{
    /// <summary>
    /// All discovered groups, sorted ascending by group number (GG).
    /// </summary>
    public IReadOnlyList<int> Groups { get; }

    /// <summary>
    /// All discovered steps, sorted globally segment-wise numerically (GG, SS, TT).
    /// </summary>
    public IReadOnlyList<StepDescriptor> Steps { get; }

    /// <summary>
    /// Loader warnings collected during discovery across all script folders.
    /// An empty list means no convention violations were detected.
    /// </summary>
    public IReadOnlyList<LoaderWarning> Warnings { get; }

    internal ScriptContext(
        IReadOnlyList<int> groups,
        IReadOnlyList<StepDescriptor> steps,
        IReadOnlyList<LoaderWarning> warnings)
    {
        Groups = groups;
        // Wrap in true read-only wrappers so callers cannot cast back to List<T> and mutate.
        Steps = steps is List<StepDescriptor> sl ? sl.AsReadOnly() : steps;
        Warnings = warnings is List<LoaderWarning> wl ? wl.AsReadOnly() : warnings;
    }
}
