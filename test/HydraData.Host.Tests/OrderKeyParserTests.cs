// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Host.Tests;

public sealed class OrderKeyParserTests
{
    [Theory]
    [InlineData("01_20", 1, 20, null)]
    [InlineData("01_20_01", 1, 20, 1)]
    [InlineData("  01_20  ", 1, 20, null)]
    [InlineData("1_2", 1, 2, null)]
    public void TryParse_accepts_unsigned_order_keys(
        string text,
        int expectedGroup,
        int expectedStep,
        int? expectedSubStep)
    {
        var parsed = OrderKeyParser.TryParse(text, out var order);

        Assert.True(parsed);
        Assert.NotNull(order);
        Assert.Equal(expectedGroup, order.Group);
        Assert.Equal(expectedStep, order.Step);
        Assert.Equal(expectedSubStep, order.SubStep);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("01_")]
    [InlineData("_20")]
    [InlineData("01__20")]
    [InlineData("1")]
    [InlineData("1_2_3_4")]
    [InlineData("-1_10")]
    [InlineData("01_-1")]
    [InlineData("+1_20")]
    [InlineData("99999999999_20")]
    public void TryParse_rejects_invalid_order_keys(string? text)
    {
        var parsed = OrderKeyParser.TryParse(text, out var order);

        Assert.False(parsed);
        Assert.Null(order);
    }
}
