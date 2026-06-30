// Copyright (c) 2026 crossVault GmbH.

using System.Globalization;

namespace HydraData.Engine;

/// <summary>
/// Narrow set of pure helper functions available in every script via <c>using static Fn;</c>.
/// Names follow lowerCamelCase so they read like operators or keywords inside data-pump scripts
/// (inspiration: M <c>if/then/else</c>, PySpark <c>when/otherwise</c>), deliberately deviating
/// from the PascalCase convention used on <c>PumpContext</c> methods.
/// <para>
/// The class is <see langword="partial"/> so additional helpers can be added in later
/// clusters without modifying this file. Naming rules for extensions: lowerCamelCase,
/// verb- or operator-oriented, null-behaviour explicit in the name,
/// <see cref="CultureInfo.InvariantCulture"/> on every conversion.
/// </para>
/// </summary>
/// <remarks>
/// T06.3 — Conscious non-inclusions:
/// <list type="bullet">
/// <item>No free synonym aliases (<c>firstNonNull</c>, <c>isNullOrBlank</c>, <c>formatText</c>).
/// Two names for the same function violate the narrow-core rule.</item>
/// <item>No builder pattern for <c>icase</c> (<c>.When().Otherwise()</c>) — YAGNI;
/// the params signature covers the need.</item>
/// <item>No params-only <c>icase</c> variant without <c>elseValue</c>: a missed case would
/// silently return <c>default(T)</c>, violating "null behaviour never silently ignored"
///.</item>
/// <item>No broad date/text library; .NET <c>DateTime</c>/<c>string</c> methods suffice.
/// Only helpers that read like operators in the script flow are admitted.</item>
/// <item>No DataFrame/with_columns helper — the engine works via DuckDB/Analyze, not a
/// custom DataFrame fluent API.</item>
/// </list>
/// M-style aliases are only available via <c>M.*</c>; no free top-level PascalCase classes
/// (<c>Guid</c>, <c>DateTime</c>, <c>List</c>) are introduced.
/// </remarks>
public static partial class Fn
{
    // -------------------------------------------------------------------------
    // T06.2 – Core helpers v1
    // -------------------------------------------------------------------------

    /// <summary>
    /// Eager conditional expression — both branches are evaluated before the call.
    /// Use when the result must be an expression (e.g. inside a lambda or initializer).
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="cond">Condition to test.</param>
    /// <param name="then">Value returned when <paramref name="cond"/> is <see langword="true"/>.</param>
    /// <param name="otherwise">Value returned when <paramref name="cond"/> is <see langword="false"/>.</param>
    /// <returns><paramref name="then"/> or <paramref name="otherwise"/>.</returns>
    public static T iif<T>(bool cond, T then, T otherwise) =>
        cond ? then : otherwise;

    /// <summary>
    /// Multi-branch conditional — returns the value of the first case whose condition is
    /// <see langword="true"/>; if no case matches, returns <paramref name="elseValue"/>.
    /// <para>
    /// <paramref name="elseValue"/> is mandatory: a silent <c>default(T)</c> on a missed case
    /// would violate "null behaviour never silently ignored".
    /// </para>
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="elseValue">Fallback value when no case condition is <see langword="true"/>.</param>
    /// <param name="cases">
    /// Ordered list of <c>(bool condition, T value)</c> pairs.
    /// The first pair with a <see langword="true"/> condition wins.
    /// </param>
    /// <returns>Matched case value, or <paramref name="elseValue"/>.</returns>
    public static T icase<T>(T elseValue, params (bool cond, T value)[] cases)
    {
        foreach (var (cond, value) in cases)
            if (cond) return value;
        return elseValue;
    }

    /// <summary>
    /// Returns the first non-<see langword="null"/> element, or <see langword="null"/> if all
    /// elements are <see langword="null"/>.
    /// </summary>
    /// <typeparam name="T">Element type (reference or nullable value type).</typeparam>
    /// <param name="values">Candidate values, evaluated left to right.</param>
    /// <returns>First non-<see langword="null"/> value, or <see langword="null"/>.</returns>
    public static T? coalesce<T>(params T?[] values)
    {
        foreach (var v in values)
            if (v is not null) return v;
        return default;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the string is <see langword="null"/>, empty,
    /// or consists only of whitespace characters.
    /// </summary>
    /// <param name="s">String to test.</param>
    /// <returns><see langword="true"/> for null/empty/whitespace; <see langword="false"/> otherwise.</returns>
    public static bool isBlank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>
    /// Formats a string using <see cref="CultureInfo.InvariantCulture"/>.
    /// This is a pure string helper — it does <em>not</em> print or log anything.
    /// Pass the result to <c>Print</c>, <c>Note</c>, or <c>Log</c> to produce output.
    /// </summary>
    /// <param name="format">A composite format string (same syntax as <see cref="string.Format(string,object?[])"/>).</param>
    /// <param name="args">Arguments to substitute into <paramref name="format"/>.</param>
    /// <returns>The formatted string.</returns>
    public static string fmt(string format, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, format, args);

    // -------------------------------------------------------------------------
    // T06.2.1 – parseOr<T>
    // -------------------------------------------------------------------------

    /// <summary>
    /// Culture-invariant text-to-<typeparamref name="T"/> conversion with a deterministic
    /// fallback. Never throws on parse failures of supported types — null, empty, whitespace,
    /// and unparseable input all return <paramref name="fallback"/>. For <see cref="double"/>, a
    /// non-finite result (±Infinity or NaN, e.g. from exponent overflow such as <c>"1E400"</c> or the
    /// literal <c>"NaN"</c>) is also treated as a failure and returns <paramref name="fallback"/>.
    /// <para>
    /// Supported types: <see cref="int"/>, <see cref="long"/>, <see cref="decimal"/>,
    /// <see cref="double"/>, <see cref="bool"/>, <see cref="Guid"/>, <see cref="DateTime"/>,
    /// and their <see cref="Nullable{T}"/> forms.
    /// For nullable types, a null/empty/whitespace <paramref name="text"/> returns
    /// <paramref name="fallback"/> (not <see langword="null"/> unless <paramref name="fallback"/>
    /// itself is <see langword="null"/>).
    /// </para>
    /// </summary>
    /// <typeparam name="T">Target type.</typeparam>
    /// <param name="text">Input text (may be null).</param>
    /// <param name="fallback">Value returned on any parse failure.</param>
    /// <returns>Parsed value or <paramref name="fallback"/>.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown at runtime when <typeparamref name="T"/> is not one of the supported types listed
    /// above. The "never throws" guarantee applies only to parse failures of supported types;
    /// passing an unsupported <typeparamref name="T"/> always throws.
    /// </exception>
    public static T parseOr<T>(string? text, T fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        var trimmed = text.Trim();

        // Nullable unwrapping: if T is Nullable<U>, delegate to the inner type.
        var targetType = typeof(T);
        var underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying is not null)
            return (T)ParseOrBoxed(trimmed, underlying, fallback!);

        return (T)ParseOrBoxed(trimmed, targetType, fallback!);
    }

    private static object ParseOrBoxed(string trimmed, Type type, object fallback)
    {
        if (type == typeof(int))
            return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

        if (type == typeof(long))
            return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : fallback;

        if (type == typeof(decimal))
            return decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var d) ? d : fallback;

        if (type == typeof(double))
            // A non-finite result (±Infinity from exponent overflow, or the literals "NaN"/"Infinity")
            // is treated as a parse failure → fallback: for a data pump, Infinity/NaN is the symptom of
            // bad input, never a value a script wants to carry downstream.
            return double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dbl) && double.IsFinite(dbl) ? dbl : fallback;

        if (type == typeof(bool))
            return bool.TryParse(trimmed, out var b) ? b : fallback;

        if (type == typeof(Guid))
            return Guid.TryParse(trimmed, out var g) ? g : fallback;

        if (type == typeof(DateTime))
            return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : fallback;

        throw new NotSupportedException(
            $"parseOr<T> does not support type '{type.FullName}'. " +
            "Supported: int, long, decimal, double, bool, Guid, DateTime (and their nullable forms).");
    }

    // -------------------------------------------------------------------------
    // T06.2.2 – between<T> and isIn<T>
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> when <c>lo &lt;= value &lt;= hi</c> (both bounds inclusive).
    /// Returns <see langword="false"/> — without throwing — when <c>lo &gt; hi</c> (swapped bounds).
    /// </summary>
    /// <typeparam name="T">Comparable element type.</typeparam>
    /// <param name="value">Value to test. Must not be <see langword="null"/>; passing null will throw a <see cref="NullReferenceException"/> via <see cref="IComparable{T}.CompareTo"/>.</param>
    /// <param name="lo">Lower bound (inclusive). Must not be <see langword="null"/>.</param>
    /// <param name="hi">Upper bound (inclusive). Must not be <see langword="null"/>.</param>
    /// <returns><see langword="true"/> iff the value is within the closed interval.</returns>
    public static bool between<T>(T value, T lo, T hi) where T : IComparable<T>
    {
        // Swapped bounds → documented false, no throw.
        if (lo.CompareTo(hi) > 0) return false;
        return value.CompareTo(lo) >= 0 && value.CompareTo(hi) <= 0;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is contained in
    /// <paramref name="set"/>, using <see cref="EqualityComparer{T}.Default"/>.
    /// An empty <paramref name="set"/> always returns <see langword="false"/>.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="value">Value to search for.</param>
    /// <param name="set">Candidate values.</param>
    /// <returns><see langword="true"/> iff the value is found.</returns>
    public static bool isIn<T>(T value, params T[] set)
    {
        var comparer = EqualityComparer<T>.Default;
        foreach (var item in set)
            if (comparer.Equals(value, item)) return true;
        return false;
    }

    // -------------------------------------------------------------------------
    // T06.2.3 – coalesceBlank
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the first element that is neither <see langword="null"/> nor blank
    /// (see <see cref="isBlank"/>), or <see langword="null"/> if all values are blank.
    /// <para>
    /// Use instead of <see cref="coalesce{T}"/> when empty strings from Excel cells
    /// should be skipped alongside <see langword="null"/> values.
    /// </para>
    /// </summary>
    /// <param name="values">Candidate strings, evaluated left to right.</param>
    /// <returns>First non-blank string, or <see langword="null"/>.</returns>
    public static string? coalesceBlank(params string?[] values)
    {
        foreach (var v in values)
            if (!isBlank(v)) return v;
        return null;
    }

    // -------------------------------------------------------------------------
    // T06.2.4 – nullIf<T>
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="value"/> equals
    /// <paramref name="sentinel"/>; otherwise returns <paramref name="value"/> unchanged.
    /// SQL <c>NULLIF</c> equivalent — normalises sentinel/placeholder values to
    /// <see langword="null"/> before database inserts.
    /// </summary>
    /// <typeparam name="T">Reference type.</typeparam>
    /// <param name="value">Input value (may be <see langword="null"/>).</param>
    /// <param name="sentinel">The placeholder value that should become <see langword="null"/>.</param>
    /// <returns><see langword="null"/> on equality; <paramref name="value"/> otherwise.</returns>
    public static T? nullIf<T>(T? value, T sentinel) where T : class
    {
        if (value is null) return null;
        return EqualityComparer<T>.Default.Equals(value, sentinel) ? null : value;
    }

    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="value"/> equals
    /// <paramref name="sentinel"/>; otherwise returns <paramref name="value"/> unchanged.
    /// Value-type overload of <see cref="nullIf{T}(T?,T)"/>.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="value">Input value.</param>
    /// <param name="sentinel">The placeholder value that should become <see langword="null"/>.</param>
    /// <returns><see langword="null"/> on equality; <paramref name="value"/> otherwise.</returns>
    public static T? nullIf<T>(T? value, T sentinel) where T : struct =>
        value.HasValue && EqualityComparer<T>.Default.Equals(value.Value, sentinel) ? null : value;

    // -------------------------------------------------------------------------
    // T06.2.5 – trimToNull and cleanText
    // -------------------------------------------------------------------------

    /// <summary>
    /// Trims leading and trailing whitespace and returns <see langword="null"/> when the
    /// result is empty. <see langword="null"/> or whitespace-only input returns
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="text">Input text (may be <see langword="null"/>).</param>
    /// <returns>Trimmed string, or <see langword="null"/>.</returns>
    public static string? trimToNull(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Removes non-printable control characters (Unicode categories
    /// <see cref="UnicodeCategory.Control"/> and <see cref="UnicodeCategory.OtherNotAssigned"/>)
    /// from the string. Space (U+0020, <see cref="UnicodeCategory.SpaceSeparator"/>) is preserved;
    /// tab (U+0009) and other <see cref="UnicodeCategory.Control"/> characters are removed.
    /// <see langword="null"/> input returns <see langword="null"/>.
    /// </summary>
    /// <param name="text">Input text (may be <see langword="null"/>).</param>
    /// <returns>Cleaned string, or <see langword="null"/>.</returns>
    public static string? cleanText(string? text)
    {
        if (text is null) return null;

        // Fast path: scan first to see if any character needs removal.
        var needsCleaning = false;
        foreach (var ch in text)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.Control || cat == UnicodeCategory.OtherNotAssigned)
            {
                needsCleaning = true;
                break;
            }
        }

        if (!needsCleaning) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (cat != UnicodeCategory.Control && cat != UnicodeCategory.OtherNotAssigned)
                sb.Append(ch);
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // T06.2.6 – Delimiter helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the portion of <paramref name="text"/> that appears before the first
    /// occurrence of <paramref name="delimiter"/>. Returns <paramref name="fallback"/>
    /// when <paramref name="text"/> is <see langword="null"/>, <paramref name="delimiter"/>
    /// is null or empty, or the delimiter is not found.
    /// </summary>
    /// <param name="text">Source string (may be <see langword="null"/>).</param>
    /// <param name="delimiter">Delimiter to search for.</param>
    /// <param name="fallback">Value returned when the delimiter is not found.</param>
    /// <returns>Substring before the first delimiter, or <paramref name="fallback"/>.</returns>
    public static string? beforeDelimiter(string? text, string delimiter, string? fallback = null)
    {
        if (text is null || string.IsNullOrEmpty(delimiter)) return fallback;
        var idx = text.IndexOf(delimiter, StringComparison.Ordinal);
        return idx < 0 ? fallback : text[..idx];
    }

    /// <summary>
    /// Returns the portion of <paramref name="text"/> that appears after the first
    /// occurrence of <paramref name="delimiter"/>. Returns <paramref name="fallback"/>
    /// when <paramref name="text"/> is <see langword="null"/>, <paramref name="delimiter"/>
    /// is null or empty, or the delimiter is not found.
    /// </summary>
    /// <param name="text">Source string (may be <see langword="null"/>).</param>
    /// <param name="delimiter">Delimiter to search for.</param>
    /// <param name="fallback">Value returned when the delimiter is not found.</param>
    /// <returns>Substring after the first delimiter, or <paramref name="fallback"/>.</returns>
    public static string? afterDelimiter(string? text, string delimiter, string? fallback = null)
    {
        if (text is null || string.IsNullOrEmpty(delimiter)) return fallback;
        var idx = text.IndexOf(delimiter, StringComparison.Ordinal);
        return idx < 0 ? fallback : text[(idx + delimiter.Length)..];
    }

    /// <summary>
    /// Returns the portion of <paramref name="text"/> between the first occurrence of
    /// <paramref name="startDelimiter"/> and the first subsequent occurrence of
    /// <paramref name="endDelimiter"/>. Returns <paramref name="fallback"/> when any input
    /// is <see langword="null"/>, either delimiter is empty, or either delimiter is not found
    /// in the expected order.
    /// </summary>
    /// <param name="text">Source string (may be <see langword="null"/>).</param>
    /// <param name="startDelimiter">Opening delimiter.</param>
    /// <param name="endDelimiter">Closing delimiter; searched after the start delimiter.</param>
    /// <param name="fallback">Value returned when extraction fails.</param>
    /// <returns>Substring between the delimiters, or <paramref name="fallback"/>.</returns>
    public static string? betweenDelimiters(
        string? text, string startDelimiter, string endDelimiter, string? fallback = null)
    {
        if (text is null || string.IsNullOrEmpty(startDelimiter) || string.IsNullOrEmpty(endDelimiter))
            return fallback;

        var startIdx = text.IndexOf(startDelimiter, StringComparison.Ordinal);
        if (startIdx < 0) return fallback;

        var afterStart = startIdx + startDelimiter.Length;
        var endIdx = text.IndexOf(endDelimiter, afterStart, StringComparison.Ordinal);
        if (endIdx < 0) return fallback;

        return text[afterStart..endIdx];
    }
}
