// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Xunit;

namespace HydraData.Engine.Tests;

public class GuidProviderTests
{
    [Fact]
    public void System_provider_returns_distinct_nonempty_guids()
    {
        var sut = SystemGuidProvider.Instance;
        var a = sut.NewGuid();
        var b = sut.NewGuid();

        Assert.NotEqual(Guid.Empty, a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Fake_provider_seam_yields_fixed_guid()
    {
        // Demonstrates the determinism seam used by RunId tests (T02.7 / T08.2).
        var fixedId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        IGuidProvider sut = new FixedGuidProvider(fixedId);

        Assert.Equal(fixedId, sut.NewGuid());
        Assert.Equal(fixedId, sut.NewGuid());
    }

    private sealed class FixedGuidProvider(Guid value) : IGuidProvider
    {
        public Guid NewGuid() => value;
    }
}
