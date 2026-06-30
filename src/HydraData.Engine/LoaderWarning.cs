// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Identifies the kind of issue detected by <see cref="StepLoader"/>.
/// </summary>
public enum LoaderWarningKind
{
    /// <summary>Two step files share the identical numeric order (GG, SS, TT).</summary>
    DuplicateOrder,

    /// <summary>A filename contains an opening bracket <c>[</c> with no valid closing bracket.</summary>
    InvalidTag,

    /// <summary>
    /// Steps belonging to the same group (GG) are not contiguous in sort order
    /// because a step from a different group appears between them.
    /// </summary>
    NonContiguousGroup,

    /// <summary>Steps in the same group carry different slug labels.</summary>
    SlugInconsistency,
}

/// <summary>
/// A diagnostic message emitted by <see cref="StepLoader"/> when a filename
/// convention violation is detected.
/// </summary>
/// <param name="Kind">The category of the warning.</param>
/// <param name="Message">Human-readable explanation including the affected filenames.</param>
public sealed record LoaderWarning(
    LoaderWarningKind Kind,
    string Message);
