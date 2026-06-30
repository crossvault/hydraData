// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Runtime switches that control <see cref="StepLoader"/> behaviour.
/// Both switches default to <see langword="false"/> (new schema).
/// </summary>
public sealed class LoaderOptions
{
    /// <summary>
    /// When <see langword="true"/>, the loader groups steps by their slug label
    /// rather than by the leading numeric group segment (GG).
    /// This is a temporary migration switch for codebases that were written
    /// against the legacy slug-grouping schema; leave <see langword="false"/> for
    /// all new work.
    /// </summary>
    public bool LegacyGroupBySlug { get; init; }

    /// <summary>
    /// When <see langword="true"/>, a single <see cref="PumpState"/> is shared
    /// across all groups for the entire run instead of being scoped per group.
    /// This is a temporary migration switch; leave <see langword="false"/> for
    /// all new work.
    /// </summary>
    public bool LegacyGlobalState { get; init; }
}
