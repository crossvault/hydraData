// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// The parsed, numeric ordering key extracted from a step filename.
/// Segments are sorted numerically (not lexicographically): GG, then SS, then TT.
/// </summary>
/// <param name="Group">Leading numeric segment — the group key (GG).</param>
/// <param name="Step">Second numeric segment — the step within the group (SS).</param>
/// <param name="SubStep">Optional third numeric segment — the sub-step (TT), or <see langword="null"/>.</param>
/// <param name="Slug">Optional bracketed label in the original filename, e.g. <c>[kunden]</c>.</param>
public sealed record StepOrder(int Group, int Step, int? SubStep, string? Slug)
    : IComparable<StepOrder>
{
    /// <inheritdoc />
    public int CompareTo(StepOrder? other)
    {
        if (other is null) return 1;
        int c = Group.CompareTo(other.Group);
        if (c != 0) return c;
        c = Step.CompareTo(other.Step);
        if (c != 0) return c;
        // null sub-step sorts before any non-null value
        if (SubStep is null && other.SubStep is null) return 0;
        if (SubStep is null) return -1;
        if (other.SubStep is null) return 1;
        return SubStep.Value.CompareTo(other.SubStep.Value);
    }
}
