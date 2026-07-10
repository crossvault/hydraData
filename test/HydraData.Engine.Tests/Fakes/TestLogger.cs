// Copyright (c) 2026 crossVault GmbH.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HydraData.Engine.Tests.Fakes;

/// <summary>
/// A capturing <see cref="ILogger"/> sink for tests: records each entry's level, rendered message and the
/// active scope stack so assertions can check scope contents (RunId, ScriptName) and that no
/// ConnectionString is ever logged (T07.1–T07.3).
/// </summary>
internal sealed class TestLogger : ILogger
{
    private readonly AsyncLocal<ScopeNode?> _currentScope = new();

    /// <summary>All recorded entries, in log order.</summary>
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    /// <summary>Returns every active scope state object captured for any entry, flattened.</summary>
    public IEnumerable<object> AllScopeStates =>
        Entries.SelectMany(e => e.Scopes);

    /// <summary>The number of scopes currently active on this asynchronous flow.</summary>
    public int ActiveScopeCount
    {
        get
        {
            var count = 0;
            for (var node = _currentScope.Value; node is not null; node = node.Parent)
                count++;
            return count;
        }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        var node = new ScopeNode(state, _currentScope.Value, this);
        _currentScope.Value = node;
        return node;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var scopes = new List<object>();
        for (var node = _currentScope.Value; node is not null; node = node.Parent)
            scopes.Add(node.State);
        scopes.Reverse();

        Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), scopes, exception));
    }

    /// <summary>Returns the values stored under <paramref name="key"/> across all captured scopes.</summary>
    public IEnumerable<object?> ScopeValues(string key)
    {
        foreach (var state in AllScopeStates)
        {
            if (state is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                foreach (var pair in pairs)
                    if (pair.Key == key)
                        yield return pair.Value;
            }
        }
    }

    private sealed class ScopeNode(object state, ScopeNode? parent, TestLogger owner) : IDisposable
    {
        public object State { get; } = state;
        public ScopeNode? Parent { get; } = parent;

        public void Dispose() => owner._currentScope.Value = Parent;
    }
}

/// <summary>One captured log entry.</summary>
/// <param name="Level">The log level.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="Scopes">The active scope state objects (outermost first) at log time.</param>
/// <param name="Exception">The exception, if any.</param>
internal sealed record LogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyList<object> Scopes,
    Exception? Exception);
