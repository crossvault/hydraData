// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Host.Tests;

public sealed class HostExitTests
{
    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(System.Xml.XmlException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(System.Text.Json.JsonException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task Config_exception_writes_exactly_one_configuration_error_line(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "bad config")!;
        var error = new StringWriter();

        var exit = await HostExit.MapConfigExceptionAsync(exception, logger: null, error);

        Assert.Equal(1, exit);
        var line = Assert.Single(Lines(error));
        Assert.StartsWith("Configuration error:", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_exception_writes_exactly_one_typed_error_line()
    {
        var error = new StringWriter();

        var exit = await HostExit.MapConfigExceptionAsync(
            new InvalidCastException("bad cast"),
            logger: null,
            error);

        Assert.Equal(1, exit);
        var line = Assert.Single(Lines(error));
        Assert.StartsWith("Unexpected host error: InvalidCastException:", line, StringComparison.Ordinal);
    }

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
}
