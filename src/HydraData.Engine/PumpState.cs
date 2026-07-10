// Copyright (c) 2026 crossVault GmbH.

using System.Collections.ObjectModel;

namespace HydraData.Engine;

/// <summary>
/// Case-insensitive key/value bag for passing data between steps within a group.
/// Each group receives its own <see cref="PumpState"/> scope; a run-global bag is
/// provided separately via <c>Shared</c> on <c>PumpContext</c>.
/// </summary>
public sealed class PumpState
{
    private readonly Dictionary<string, object?> _data =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets or overwrites the value for <paramref name="key"/> (case-insensitive).</summary>
    public void Set(string key, object? value) => _data[key] = value;

    /// <summary>Returns <see langword="true"/> if <paramref name="key"/> exists (case-insensitive).</summary>
    public bool Has(string key) => _data.ContainsKey(key);

    /// <summary>
    /// Returns the value for <paramref name="key"/> cast to <typeparamref name="T"/>,
    /// or <see langword="default"/> if the key does not exist or its value is <see langword="null"/>.
    /// </summary>
    /// <exception cref="InvalidCastException">A non-null value has an incompatible type.</exception>
    public T? Get<T>(string key) =>
        _data.TryGetValue(key, out var value) && value is not null ? (T?)value : default;

    /// <summary>
    /// Returns the value for <paramref name="key"/> cast to <typeparamref name="T"/>.
    /// Throws <see cref="KeyNotFoundException"/> with a clear message when the key does not exist.
    /// Throws <see cref="InvalidOperationException"/> with a clear message when the key exists but its
    /// value is <see langword="null"/> or cannot satisfy <typeparamref name="T"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The key was not found in this state scope.</exception>
    /// <exception cref="InvalidOperationException">
    /// The key exists but its value is <see langword="null"/> or has an incompatible type.
    /// </exception>
    public T Require<T>(string key)
    {
        if (!_data.TryGetValue(key, out var value))
            throw new KeyNotFoundException(
                $"PumpState: required key '{key}' not found. " +
                $"Available keys: [{string.Join(", ", _data.Keys)}]");
        if (value is T typed)
            return typed;

        var heldType = value is null ? "null" : value.GetType().FullName ?? value.GetType().Name;
        throw new InvalidOperationException(
            $"PumpState: key '{key}' holds {heldType}; cannot satisfy Require<{typeof(T).Name}>.");
    }

    /// <summary>
    /// Returns an independent, immutable snapshot of the current state.
    /// Mutations to the original or the snapshot do not affect each other.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Snapshot() =>
        new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(_data, StringComparer.OrdinalIgnoreCase));
}
