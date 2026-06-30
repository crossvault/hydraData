// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine.Tests.Fakes;

/// <summary>A deterministic <see cref="IGuidProvider"/> returning a fixed GUID (used by RunId tests).</summary>
internal sealed class FakeGuidProvider(Guid value) : IGuidProvider
{
    /// <summary>The fixed GUID returned by every call.</summary>
    public Guid Value { get; } = value;

    /// <inheritdoc />
    public Guid NewGuid() => Value;
}
