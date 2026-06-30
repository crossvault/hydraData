// Copyright (c) 2026 crossVault GmbH.

using System.Text;
using Microsoft.Extensions.Logging;

namespace HydraData.Host;

/// <summary>
/// A deliberately small <see cref="ILoggerProvider"/> that appends structured log lines to a file under the
/// workspace (T07.4: "Console + Datei"). It avoids pulling in a full logging framework (Serilog et al.) for
/// the host's modest needs: one line per entry, ISO timestamp, level, category, message.
/// </summary>
/// <remarks>
/// <para>
/// Writes are serialised behind a lock; this host runs steps sequentially, so contention is negligible. The
/// provider never logs connection strings — that responsibility sits with the engine and
/// <see cref="ConnectionRegistry"/>, which only ever surface connection <em>ids</em>.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly LogLevel _minLevel;
    private readonly StreamWriter _writer;
    private bool _disposed;

    /// <summary>Creates a provider writing to <paramref name="path"/>.</summary>
    /// <param name="path">The log file path. Its directory is created if missing.</param>
    /// <param name="minLevel">Minimum level to write. Defaults to <see cref="LogLevel.Information"/>.</param>
    public FileLoggerProvider(string path, LogLevel minLevel = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _minLevel = minLevel;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // One long-lived append writer for the provider's lifetime; reopening per line throttled verbose runs.
        _writer = new StreamWriter(path, append: true, Encoding.UTF8);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private void Append(string line)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ArgumentNullException.ThrowIfNull(formatter);

            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.UtcNow:o} [{logLevel}] {category}: {message}";
            if (exception is not null)
                line += $"{Environment.NewLine}{exception}";

            provider.Append(line);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
            // No-op: this minimal provider does not render scopes.
        }
    }
}
