// Copyright (c) 2026 crossVault GmbH.

using Microsoft.Extensions.Logging;

namespace HydraData.Host;

/// <summary>
/// Shared exit-code mapping helper used by both <see cref="HostBootstrap"/> (full-run) and
/// <see cref="SessionBootstrap"/> (session) for the configuration-failure and catch-all catch ladders.
/// The cancellation exit code differs between modes and is therefore NOT handled here.
/// </summary>
internal static class HostExit
{
    /// <summary>
    /// Maps a configuration or unexpected exception to an exit code and writes a single-line
    /// message to <paramref name="err"/>.
    /// </summary>
    /// <param name="ex">The exception to map.</param>
    /// <param name="logger">Optional logger; when non-null the exception is also logged at Error.</param>
    /// <param name="err">Output writer that receives the human-readable error line.</param>
    /// <returns>
    /// <c>1</c> for known configuration-class exceptions; <c>1</c> for any other unexpected exception
    /// that is not <see cref="OutOfMemoryException"/> or <see cref="StackOverflowException"/>
    /// (those two are intentionally not caught here — let the runtime handle them).
    /// </returns>
    internal static async Task<int> MapConfigExceptionAsync(
        Exception ex,
        ILogger? logger,
        TextWriter err)
    {
        if (ex is FileNotFoundException
            or DirectoryNotFoundException
            or System.Xml.XmlException
            or FormatException
            or InvalidOperationException
            or System.Text.Json.JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            var message = $"Configuration error: {ex.Message}";
            logger?.LogError(ex, "Configuration/preflight failure; the run did not start.");
            await err.WriteLineAsync(message).ConfigureAwait(false);
            return 1;
        }

        if (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var message = $"Unexpected host error: {ex.GetType().Name}: {ex.Message}";
            logger?.LogError(ex, "Unexpected host error; the run did not complete.");
            await err.WriteLineAsync(message).ConfigureAwait(false);
            return 1;
        }

        // OutOfMemoryException / StackOverflowException — re-throw so the runtime handles them.
        throw ex;
    }
}
