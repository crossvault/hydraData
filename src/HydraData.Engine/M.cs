// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// C#-safe Power-Query-M-style alias façade.
/// Scripts can read <c>M.Text.Format(...)</c>, <c>M.Value.FromText&lt;T&gt;(...)</c>, etc.,
/// which is closer to the Power Query M authoring experience without introducing top-level
/// type name collisions with <c>System.Guid</c>, <c>System.DateTime</c>, or
/// <c>System.Collections.Generic.List&lt;T&gt;</c>.
/// <para>
/// Every alias delegates to the canonical <see cref="Fn"/> helper — no logic is duplicated.
/// </para>
/// </summary>
/// <remarks>
/// Accepted aliases only: the documented script helper set.
/// No additional free top-level PascalCase classes are introduced.
/// </remarks>
public static class M
{
    /// <summary>
    /// Aliases for Power Query <c>Text.*</c> functions.
    /// </summary>
    public static class Text
    {
        /// <summary>
        /// Alias for <see cref="Fn.fmt"/>: formats a string with
        /// <see cref="System.Globalization.CultureInfo.InvariantCulture"/>.
        /// Does not print — pass the result to <c>Print</c>/<c>Note</c>/<c>Log</c>.
        /// </summary>
        /// <param name="format">Composite format string.</param>
        /// <param name="args">Format arguments.</param>
        /// <returns>Formatted string.</returns>
        public static string Format(string format, params object?[] args) =>
            Fn.fmt(format, args);

        /// <summary>
        /// Alias for <see cref="Fn.cleanText"/>: removes non-printable control characters.
        /// <see langword="null"/> input returns <see langword="null"/>.
        /// </summary>
        /// <param name="text">Input text (may be <see langword="null"/>).</param>
        /// <returns>Cleaned string, or <see langword="null"/>.</returns>
        public static string? Clean(string? text) => Fn.cleanText(text);

        /// <summary>
        /// Alias for <see cref="Fn.trimToNull"/>: trims whitespace and converts
        /// null/empty/whitespace to <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Intentional divergence from Power Query <c>M Text.Trim</c>: whereas the M function
        /// returns an empty string for whitespace-only input, this alias returns
        /// <see langword="null"/> (per T06.2.5 and <see cref="Fn.trimToNull"/>). The null return
        /// makes the empty-after-trim case explicit, matching the "null behaviour never silently
        /// ignored" rule.
        /// </remarks>
        /// <param name="text">Input text (may be <see langword="null"/>).</param>
        /// <returns>Trimmed string, or <see langword="null"/>.</returns>
        public static string? Trim(string? text) => Fn.trimToNull(text);

        /// <summary>
        /// Alias for <see cref="Fn.beforeDelimiter"/>: returns the portion of
        /// <paramref name="text"/> before the first occurrence of <paramref name="delimiter"/>.
        /// </summary>
        /// <param name="text">Source string (may be <see langword="null"/>).</param>
        /// <param name="delimiter">Delimiter to search for.</param>
        /// <param name="fallback">Value returned when extraction fails.</param>
        /// <returns>Substring before the delimiter, or <paramref name="fallback"/>.</returns>
        public static string? BeforeDelimiter(string? text, string delimiter, string? fallback = null) =>
            Fn.beforeDelimiter(text, delimiter, fallback);

        /// <summary>
        /// Alias for <see cref="Fn.afterDelimiter"/>: returns the portion of
        /// <paramref name="text"/> after the first occurrence of <paramref name="delimiter"/>.
        /// </summary>
        /// <param name="text">Source string (may be <see langword="null"/>).</param>
        /// <param name="delimiter">Delimiter to search for.</param>
        /// <param name="fallback">Value returned when extraction fails.</param>
        /// <returns>Substring after the delimiter, or <paramref name="fallback"/>.</returns>
        public static string? AfterDelimiter(string? text, string delimiter, string? fallback = null) =>
            Fn.afterDelimiter(text, delimiter, fallback);

        /// <summary>
        /// Alias for <see cref="Fn.betweenDelimiters"/>: returns the portion of
        /// <paramref name="text"/> between the first <paramref name="startDelimiter"/> and the
        /// first subsequent <paramref name="endDelimiter"/>.
        /// </summary>
        /// <param name="text">Source string (may be <see langword="null"/>).</param>
        /// <param name="startDelimiter">Opening delimiter.</param>
        /// <param name="endDelimiter">Closing delimiter.</param>
        /// <param name="fallback">Value returned when extraction fails.</param>
        /// <returns>Substring between the delimiters, or <paramref name="fallback"/>.</returns>
        public static string? BetweenDelimiters(
            string? text, string startDelimiter, string endDelimiter, string? fallback = null) =>
            Fn.betweenDelimiters(text, startDelimiter, endDelimiter, fallback);
    }

    /// <summary>
    /// Aliases for Power Query <c>Value.*</c> functions.
    /// </summary>
    public static class Value
    {
        /// <summary>
        /// Alias for <see cref="Fn.parseOr{T}"/>: culture-invariant text-to-<typeparamref name="T"/>
        /// conversion with a deterministic fallback. Never throws.
        /// </summary>
        /// <typeparam name="T">
        /// Target type: <see cref="int"/>, <see cref="long"/>, <see cref="decimal"/>,
        /// <see cref="double"/>, <see cref="bool"/>, <see cref="System.Guid"/>,
        /// <see cref="System.DateTime"/>, or their nullable forms.
        /// </typeparam>
        /// <param name="text">Input text (may be <see langword="null"/>).</param>
        /// <param name="fallback">Value returned on any parse failure.</param>
        /// <returns>Parsed value or <paramref name="fallback"/>.</returns>
        public static T FromText<T>(string? text, T fallback) => Fn.parseOr<T>(text, fallback);
    }

    /// <summary>
    /// Aliases for Power Query <c>Number.*</c> functions.
    /// </summary>
    public static class Number
    {
        /// <summary>
        /// Alias for <see cref="Fn.parseOr{T}"/> targeting numeric types.
        /// Culture-invariant parsing of <see cref="int"/> with a deterministic fallback.
        /// </summary>
        /// <param name="text">Input text (may be <see langword="null"/>).</param>
        /// <param name="fallback">Fallback value.</param>
        /// <returns>Parsed value or <paramref name="fallback"/>.</returns>
        public static int FromText(string? text, int fallback) => Fn.parseOr<int>(text, fallback);

        /// <summary>
        /// Alias for <see cref="Fn.parseOr{T}"/> targeting numeric types.
        /// Culture-invariant parsing of <see cref="long"/> with a deterministic fallback.
        /// </summary>
        /// <param name="text">Input text (may be <see langword="null"/>).</param>
        /// <param name="fallback">Fallback value.</param>
        /// <returns>Parsed value or <paramref name="fallback"/>.</returns>
        public static long FromText(string? text, long fallback) => Fn.parseOr<long>(text, fallback);

        /// <summary>
        /// Alias for <see cref="Fn.parseOr{T}"/> targeting numeric types.
        /// Culture-invariant parsing of <see cref="decimal"/> with a deterministic fallback.
        /// </summary>
        /// <param name="text">Input text (may be <see langword="null"/>).</param>
        /// <param name="fallback">Fallback value.</param>
        /// <returns>Parsed value or <paramref name="fallback"/>.</returns>
        public static decimal FromText(string? text, decimal fallback) => Fn.parseOr<decimal>(text, fallback);

        /// <summary>
        /// Alias for <see cref="Fn.parseOr{T}"/> targeting numeric types.
        /// Culture-invariant parsing of <see cref="double"/> with a deterministic fallback.
        /// </summary>
        /// <param name="text">Input text (may be <see langword="null"/>).</param>
        /// <param name="fallback">Fallback value.</param>
        /// <returns>Parsed value or <paramref name="fallback"/>.</returns>
        public static double FromText(string? text, double fallback) => Fn.parseOr<double>(text, fallback);
    }

    /// <summary>
    /// Aliases for Power Query <c>Logical.*</c> functions.
    /// </summary>
    public static class Logical
    {
        /// <summary>
        /// Alias for <see cref="Fn.parseOr{T}"/> targeting <see cref="bool"/>.
        /// Accepts the invariant forms <c>True</c>/<c>False</c>.
        /// Domain-specific mappings (<c>J/N</c>, <c>Y/N</c>, <c>1/0</c>) are intentionally
        /// excluded — they belong in script logic.
        /// </summary>
        /// <param name="text">Input text (may be <see langword="null"/>).</param>
        /// <param name="fallback">Fallback value.</param>
        /// <returns>Parsed boolean or <paramref name="fallback"/>.</returns>
        public static bool FromText(string? text, bool fallback) => Fn.parseOr<bool>(text, fallback);
    }

    /// <summary>
    /// Aliases for Power Query <c>Guid.*</c> functions.
    /// Named <c>M.Guid</c> to avoid collision with <c>System.Guid</c>.
    /// </summary>
    public static class Guid
    {
        /// <summary>
        /// Alias for <see cref="Fn.parseOr{T}"/> targeting <see cref="System.Guid"/>.
        /// Returns <paramref name="fallback"/> for any unparseable input without throwing.
        /// </summary>
        /// <param name="text">Input text (may be <see langword="null"/>).</param>
        /// <param name="fallback">Fallback value.</param>
        /// <returns>Parsed GUID or <paramref name="fallback"/>.</returns>
        public static System.Guid From(string? text, System.Guid fallback) =>
            Fn.parseOr<System.Guid>(text, fallback);
    }

    /// <summary>
    /// Aliases for Power Query <c>DateTime.*</c> functions.
    /// Named <c>M.DateTime</c> to avoid collision with <c>System.DateTime</c>.
    /// </summary>
    public static class DateTime
    {
        /// <summary>
        /// Alias for <see cref="Fn.parseOr{T}"/> targeting <see cref="System.DateTime"/>.
        /// Parsing is culture-invariant; locale-specific date formats return
        /// <paramref name="fallback"/> without throwing.
        /// </summary>
        /// <param name="text">Input text (may be <see langword="null"/>).</param>
        /// <param name="fallback">Fallback value.</param>
        /// <returns>Parsed date/time or <paramref name="fallback"/>.</returns>
        public static System.DateTime FromText(string? text, System.DateTime fallback) =>
            Fn.parseOr<System.DateTime>(text, fallback);
    }

    /// <summary>
    /// Aliases for Power Query <c>List.*</c> functions.
    /// Named <c>M.List</c> to avoid collision with <c>System.Collections.Generic.List&lt;T&gt;</c>.
    /// </summary>
    public static class List
    {
        /// <summary>
        /// Alias for <see cref="Fn.isIn{T}"/>: membership check using
        /// <see cref="EqualityComparer{T}.Default"/>.
        /// </summary>
        /// <typeparam name="T">Element type.</typeparam>
        /// <param name="value">Value to search for.</param>
        /// <param name="set">Candidate values.</param>
        /// <returns><see langword="true"/> iff <paramref name="value"/> is found in <paramref name="set"/>.</returns>
        public static bool Contains<T>(T value, params T[] set) => Fn.isIn<T>(value, set);
    }
}
