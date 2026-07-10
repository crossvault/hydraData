// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

public class ExternContextTests
{
    // ── FromValues: allowed types ────────────────────────────────────────────

    [Fact]
    public void FromValues_accepts_string() =>
        ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = "hello" });

    [Fact]
    public void FromValues_accepts_int() =>
        ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = 42 });

    [Fact]
    public void FromValues_accepts_long() =>
        ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = 99L });

    [Fact]
    public void FromValues_accepts_bool() =>
        ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = true });

    [Fact]
    public void FromValues_accepts_Guid() =>
        ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = Guid.NewGuid() });

    [Fact]
    public void FromValues_accepts_Enum() =>
        ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = Severity.Warning });

    [Fact]
    public void FromValues_accepts_decimal() =>
        ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = 1.23m });

    [Fact]
    public void FromValues_accepts_DateTimeOffset() =>
        ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = DateTimeOffset.UtcNow });

    [Fact]
    public void FromValues_accepts_null_value()
    {
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = null });
        Assert.Null(ctx.Get<string>("k"));
    }

    // ── FromValues: rejected types ───────────────────────────────────────────

    [Fact]
    public void FromValues_rejects_object()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = new object() }));
        Assert.Contains("unsupported type", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("k", ex.Message);
    }

    [Fact]
    public void FromValues_rejects_list()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = new List<int> { 1 } }));
        Assert.Contains("unsupported type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromValues_rejects_DateTime()
    {
        // DateTime is NOT in the allowed set — hosts must use DateTimeOffset.
        var ex = Assert.Throws<ArgumentException>(() =>
            ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = DateTime.UtcNow }));
        Assert.Contains("unsupported type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Read-only guarantee ──────────────────────────────────────────────────

    [Fact]
    public void ExternContext_has_no_public_setter()
    {
        // The type must not expose any setter or mutation method.
        var type = typeof(ExternContext);
        var setterMethods = type.GetMethods()
            .Where(m => m.Name.StartsWith("Set", StringComparison.Ordinal) ||
                        m.Name.StartsWith("Add", StringComparison.Ordinal) ||
                        m.Name.StartsWith("Remove", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(setterMethods);
    }

    // ── Get<T>: coercion table ───────────────────────────────────────────────

    public static TheoryData<string, object?, Type, object?> CoercionCases() => new()
    {
        // string → string
        { "str", "hello", typeof(string), "hello" },
        // int stored, retrieved as long
        { "n", 42, typeof(long), 42L },
        // long stored, retrieved as int
        { "n", 100L, typeof(int), 100 },
        // bool stored as string "true"
        { "b", "true", typeof(bool), true },
        { "b", "false", typeof(bool), false },
        { "b", "1", typeof(bool), true },
        { "b", "0", typeof(bool), false },
        // Guid from string
        { "g", "550e8400-e29b-41d4-a716-446655440000", typeof(Guid),
            Guid.Parse("550e8400-e29b-41d4-a716-446655440000") },
        // Enum from string (case-insensitive)
        { "sev", "warning", typeof(Severity), Severity.Warning },
        { "sev", "ERROR", typeof(Severity), Severity.Error },
        // decimal with period (InvariantCulture)
        { "d", "1.23", typeof(decimal), 1.23m },
        // decimal with stored decimal
        { "d", 9.99m, typeof(decimal), 9.99m },
        // DateTimeOffset from ISO string
        { "dt", "2026-01-15T10:30:00+00:00", typeof(DateTimeOffset),
            DateTimeOffset.Parse("2026-01-15T10:30:00+00:00", System.Globalization.CultureInfo.InvariantCulture) },
    };

    [Theory]
    [MemberData(nameof(CoercionCases))]
    public void Get_coerces_values_with_InvariantCulture(
        string key, object? stored, Type targetType, object? expected)
    {
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { [key] = stored });

        // Use reflection to call Get<T> with the runtime type
        var method = typeof(ExternContext).GetMethod(nameof(ExternContext.Get))!
            .MakeGenericMethod(targetType);

        var result = method.Invoke(ctx, [key]);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Get_returns_default_for_missing_key()
    {
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["other"] = 1 });
        Assert.Equal(0, ctx.Get<int>("missing"));
        Assert.Null(ctx.Get<string>("missing"));
    }

    [Fact]
    public void Get_decimal_uses_InvariantCulture_period_not_comma()
    {
        // "1,23" with a comma should fail parsing (it's not InvariantCulture decimal)
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["d"] = "1,23" });
        Assert.Throws<InvalidCastException>(() => ctx.Get<decimal>("d"));
    }

    [Fact]
    public void Get_nullable_int_coerces_using_underlying_type()
    {
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["n"] = 42L });

        Assert.Equal((int?)42, ctx.Get<int?>("n"));
    }

    [Fact]
    public void Get_nullable_decimal_coerces_using_underlying_type()
    {
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["n"] = "1.23" });

        Assert.Equal((decimal?)1.23m, ctx.Get<decimal?>("n"));
    }

    [Fact]
    public void Get_nullable_enum_coerces_using_underlying_type()
    {
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["severity"] = "warning" });

        Assert.Equal((Severity?)Severity.Warning, ctx.Get<Severity?>("severity"));
    }

    // ── Require<T> ───────────────────────────────────────────────────────────

    [Fact]
    public void Require_returns_value_when_present()
    {
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = "val" });
        Assert.Equal("val", ctx.Require<string>("k"));
    }

    [Fact]
    public void Require_throws_KeyNotFoundException_on_miss()
    {
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["existing"] = 1 });
        var ex = Assert.Throws<KeyNotFoundException>(() => ctx.Require<int>("missing"));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Require_throws_InvalidCastException_on_bad_coercion()
    {
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = "not-a-number" });
        Assert.Throws<InvalidCastException>(() => ctx.Require<int>("k"));
    }

    // ── Case-insensitivity ───────────────────────────────────────────────────

    [Fact]
    public void Get_is_case_insensitive()
    {
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["BatchDate"] = "2026-01-01" });
        Assert.Equal("2026-01-01", ctx.Get<string>("batchdate"));
        Assert.Equal("2026-01-01", ctx.Get<string>("BATCHDATE"));
    }

    [Fact]
    public void FromValues_case_insensitive_duplicate_names_the_collision()
    {
        var values = new Dictionary<string, object?>
        {
            ["BatchDate"] = "2026-01-01",
            ["batchdate"] = "2026-01-02",
        };

        var ex = Assert.Throws<ArgumentException>(() => ExternContext.FromValues(values));

        Assert.Contains("ExternContext", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BatchDate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("batchdate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("collision", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── FromValues(null) ─────────────────────────────────────────────────────

    [Fact]
    public void FromValues_null_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ExternContext.FromValues(null!));
    }

    // ── Require<T> on null-valued key ────────────────────────────────────────

    [Fact]
    public void Require_throws_InvalidCastException_for_null_valued_key()
    {
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["k"] = null });
        var ex = Assert.Throws<InvalidCastException>(() => ctx.Require<int>("k"));
        Assert.Contains("k", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── DateTimeOffset determinism: offset-less string → UTC ─────────────────

    [Fact]
    public void DateTimeOffset_offset_less_string_is_treated_as_UTC_deterministic()
    {
        // An ISO string with no offset must always coerce to the same UTC instant regardless of
        // the host's local timezone (determinism across hosts, Item 2).
        const string noOffsetString = "2026-06-24T10:00:00";
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["dt"] = noOffsetString });

        var result = ctx.Get<DateTimeOffset>("dt");

        // Must be UTC (zero offset).
        Assert.Equal(TimeSpan.Zero, result.Offset);
        // The UTC instant must be exactly the wall-clock time interpreted as UTC.
        Assert.Equal(new DateTimeOffset(2026, 6, 24, 10, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void DateTimeOffset_string_with_explicit_offset_is_adjusted_to_UTC()
    {
        // A string carrying an explicit offset (e.g. +02:00) is adjusted to UTC when
        // AdjustToUniversal is set — +02:00 means 10:00 local = 08:00 UTC.
        const string withOffset = "2026-06-24T10:00:00+02:00";
        var ctx = ExternContext.FromValues(new Dictionary<string, object?> { ["dt"] = withOffset });

        var result = ctx.Get<DateTimeOffset>("dt");

        // AdjustToUniversal normalises to UTC regardless of the original offset.
        Assert.Equal(TimeSpan.Zero, result.Offset);
        Assert.Equal(new DateTimeOffset(2026, 6, 24, 8, 0, 0, TimeSpan.Zero), result);
    }
}
