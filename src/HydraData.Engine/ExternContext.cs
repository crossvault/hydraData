// Copyright (c) 2026 crossVault GmbH.

using System.Globalization;

namespace HydraData.Engine;

/// <summary>
/// Read-only host context passed into a pump run. Contains scalar, immutable values
/// supplied by the host (e.g. batch date, tenant id). Scripts cannot write to this context.
/// </summary>
/// <remarks>
/// The only entry point is <see cref="FromValues"/>; no builder or setter is exposed.
/// </remarks>
public sealed class ExternContext
{
    private static readonly HashSet<Type> AllowedTypes =
    [
        typeof(string),
        typeof(int),
        typeof(long),
        typeof(bool),
        typeof(Guid),
        typeof(decimal),
        typeof(DateTimeOffset),
    ];

    private readonly IReadOnlyDictionary<string, object?> _values;

    private ExternContext(IReadOnlyDictionary<string, object?> values) => _values = values;

    /// <summary>
    /// Creates an <see cref="ExternContext"/> from the supplied dictionary.
    /// Only scalar/immutable types are permitted: <see cref="string"/>, <see cref="int"/>,
    /// <see cref="long"/>, <see cref="bool"/>, <see cref="Guid"/>, any <see cref="Enum"/> subtype,
    /// <see cref="decimal"/>, <see cref="DateTimeOffset"/>.
    /// Null values are allowed regardless of key type.
    /// </summary>
    /// <exception cref="ArgumentException">A value has an unsupported type.</exception>
    public static ExternContext FromValues(IDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var (key, value) in values)
        {
            if (value is null) continue;

            var type = value.GetType();
            if (!AllowedTypes.Contains(type) && !type.IsEnum)
                throw new ArgumentException(
                    $"ExternContext: key '{key}' has unsupported type '{type.FullName}'. " +
                    $"Allowed: string, int, long, bool, Guid, Enum, decimal, DateTimeOffset.",
                    nameof(values));
        }

        return new ExternContext(
            new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the value for <paramref name="key"/> coerced to <typeparamref name="T"/>
    /// using <see cref="CultureInfo.InvariantCulture"/>, or <see langword="default"/> when
    /// the key does not exist.
    /// </summary>
    public T? Get<T>(string key)
    {
        if (!_values.TryGetValue(key, out var raw)) return default;
        return Coerce<T>(key, raw);
    }

    /// <summary>
    /// Returns the value for <paramref name="key"/> coerced to <typeparamref name="T"/>.
    /// Throws <see cref="KeyNotFoundException"/> when the key does not exist and
    /// <see cref="InvalidCastException"/> when coercion fails.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The key was not found.</exception>
    /// <exception cref="InvalidCastException">The value cannot be coerced to <typeparamref name="T"/>.</exception>
    public T Require<T>(string key)
    {
        if (!_values.TryGetValue(key, out var raw))
            throw new KeyNotFoundException(
                $"ExternContext: required key '{key}' not found. " +
                $"Available keys: [{string.Join(", ", _values.Keys)}]");

        if (raw is null)
            throw new InvalidCastException(
                $"ExternContext: key '{key}' is null; cannot satisfy Require<{typeof(T).Name}>.");

        return Coerce<T>(key, raw)!;
    }

    // ── internal coercion ────────────────────────────────────────────────────

    private static T? Coerce<T>(string key, object? raw)
    {
        if (raw is null) return default;
        if (raw is T direct) return direct;

        var target = typeof(T);
        var ic = CultureInfo.InvariantCulture;

        try
        {
            // Enum
            if (target.IsEnum)
            {
                var str = raw as string ?? Convert.ToString(raw, ic)!;
                return (T)Enum.Parse(target, str, ignoreCase: true);
            }

            // Guid
            if (target == typeof(Guid))
            {
                var str = raw as string ?? Convert.ToString(raw, ic)!;
                return (T)(object)Guid.Parse(str);
            }

            // bool (handles "true"/"false"/"1"/"0" strings)
            if (target == typeof(bool))
            {
                if (raw is string s)
                {
                    if (bool.TryParse(s, out var b)) return (T)(object)b;
                    if (s == "1") return (T)(object)true;
                    if (s == "0") return (T)(object)false;
                }
                return (T)Convert.ChangeType(raw, target, ic);
            }

            // decimal (InvariantCulture, period as decimal separator; comma not allowed)
            if (target == typeof(decimal))
            {
                if (raw is string ds)
                    return (T)(object)decimal.Parse(ds,
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, ic);
                return (T)Convert.ChangeType(raw, target, ic);
            }

            // DateTimeOffset: support only string (parse with InvariantCulture) and DateTimeOffset
            // passthrough (handled by the `raw is T direct` check above).
            // DateTime is rejected by FromValues; any other type is not coercible to DateTimeOffset.
            //
            // Determinism rule: coerce every value to a UTC instant so the result depends only on the
            // input string, never on the host's local timezone. RoundtripKind would make offset-less
            // strings adopt the host's local offset (non-deterministic), which is why it is NOT used.
            // - Offset-less strings (e.g. "2026-06-24T10:00:00") are assumed UTC (AssumeUniversal).
            // - Offset-carrying strings (e.g. "2026-06-24T10:00:00+02:00") are adjusted to UTC
            //   (AdjustToUniversal): wall-clock 10:00+02:00 -> 08:00Z, offset zero.
            if (target == typeof(DateTimeOffset))
            {
                if (raw is string dtos)
                    return (T)(object)DateTimeOffset.Parse(dtos, ic,
                        System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal);
                throw new InvalidCastException(
                    $"ExternContext: cannot coerce key '{key}' " +
                    $"(value '{raw}', type '{raw.GetType().Name}') to 'DateTimeOffset': " +
                    "only string (ISO 8601) and DateTimeOffset values are supported.");
            }

            // numeric / everything else
            return (T)Convert.ChangeType(raw, target, ic);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new InvalidCastException(
                $"ExternContext: cannot coerce key '{key}' (value '{raw}', type '{raw.GetType().Name}') " +
                $"to '{target.Name}': {ex.Message}", ex);
        }
    }
}
