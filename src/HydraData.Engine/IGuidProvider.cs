// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Narrow seam for GUID creation so that <c>RunId</c> generation is deterministic in tests.
/// Time is covered by the BCL <see cref="TimeProvider"/>; no separate clock abstraction is introduced.
/// </summary>
public interface IGuidProvider
{
    /// <summary>Creates a new <see cref="Guid"/>.</summary>
    Guid NewGuid();
}

/// <summary>Default <see cref="IGuidProvider"/> delegating to <see cref="Guid.NewGuid()"/>.</summary>
public sealed class SystemGuidProvider : IGuidProvider
{
    /// <summary>A shared, stateless instance.</summary>
    public static SystemGuidProvider Instance { get; } = new();

    /// <inheritdoc />
    public Guid NewGuid() => Guid.NewGuid();
}
