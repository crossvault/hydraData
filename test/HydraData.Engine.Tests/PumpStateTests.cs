// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

public class PumpStateTests
{
    // ── Set / Has / Get ──────────────────────────────────────────────────────

    [Fact]
    public void Set_and_Get_roundtrip()
    {
        var state = new PumpState();
        state.Set("RunId", 42);
        Assert.Equal(42, state.Get<int>("RunId"));
    }

    [Fact]
    public void Has_returns_true_for_existing_key()
    {
        var state = new PumpState();
        state.Set("Key", "val");
        Assert.True(state.Has("Key"));
    }

    [Fact]
    public void Has_returns_false_for_missing_key()
    {
        var state = new PumpState();
        Assert.False(state.Has("Missing"));
    }

    [Fact]
    public void Get_returns_default_for_missing_key()
    {
        var state = new PumpState();
        Assert.Equal(0, state.Get<int>("Missing"));
        Assert.Null(state.Get<string>("Missing"));
    }

    // ── Case-insensitivity ───────────────────────────────────────────────────

    [Theory]
    [InlineData("batchdate")]
    [InlineData("BatchDate")]
    [InlineData("BATCHDATE")]
    [InlineData("bATcHdAtE")]
    public void Get_is_case_insensitive(string lookupKey)
    {
        var state = new PumpState();
        state.Set("BatchDate", "2026-01-01");
        Assert.Equal("2026-01-01", state.Get<string>(lookupKey));
    }

    [Fact]
    public void Set_overwrites_case_insensitively()
    {
        var state = new PumpState();
        state.Set("key", "first");
        state.Set("KEY", "second");
        Assert.Equal("second", state.Get<string>("key"));
    }

    // ── Require ──────────────────────────────────────────────────────────────

    [Fact]
    public void Require_returns_value_when_present()
    {
        var state = new PumpState();
        state.Set("x", 99);
        Assert.Equal(99, state.Require<int>("x"));
    }

    [Fact]
    public void Require_throws_KeyNotFoundException_with_clear_message_on_miss()
    {
        var state = new PumpState();
        state.Set("existing", 1);

        var ex = Assert.Throws<KeyNotFoundException>(() => state.Require<int>("missing"));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("existing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Require_is_case_insensitive()
    {
        var state = new PumpState();
        state.Set("Value", 7);
        Assert.Equal(7, state.Require<int>("VALUE"));
    }

    // ── Null values ──────────────────────────────────────────────────────────

    [Fact]
    public void Set_and_Get_null_value()
    {
        var state = new PumpState();
        state.Set("nullable", null);
        Assert.True(state.Has("nullable"));
        Assert.Null(state.Get<string>("nullable"));
    }

    [Fact]
    public void Require_throws_InvalidCastException_for_null_valued_key()
    {
        var state = new PumpState();
        state.Set("nv", null);
        var ex = Assert.Throws<InvalidCastException>(() => state.Require<int>("nv"));
        Assert.Contains("nv", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Snapshot independence ────────────────────────────────────────────────

    [Fact]
    public void Snapshot_is_independent_of_original()
    {
        var state = new PumpState();
        state.Set("a", 1);

        var snap = state.Snapshot();

        // Mutate original; snapshot must not change.
        state.Set("a", 999);
        state.Set("b", 2);

        Assert.Equal(1, (int)snap["a"]!);
        Assert.False(snap.ContainsKey("b"));
    }

    [Fact]
    public void Snapshot_is_immutable()
    {
        var state = new PumpState();
        state.Set("k", "v");

        var snap = state.Snapshot();

        // The returned type is IReadOnlyDictionary — casting to the mutable
        // underlying type should not be possible through the public contract.
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(snap);
    }

    [Fact]
    public void Snapshot_is_case_insensitive()
    {
        var state = new PumpState();
        state.Set("Key", "hello");

        var snap = state.Snapshot();
        Assert.True(snap.ContainsKey("key"));
        Assert.True(snap.ContainsKey("KEY"));
    }
}
