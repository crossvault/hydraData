// Copyright (c) 2026 crossVault GmbH.

using System.Globalization;
using HydraData.Engine;

namespace HydraData.Host;

/// <summary>
/// Parses a user-typed step ORDER KEY of the form <c>GG_SS</c> or <c>GG_SS_TT</c> (e.g. <c>01_20</c>,
/// <c>01_20_01</c>) into a <see cref="StepOrder"/>. Shared by the interactive session REPL
/// (<c>:run &lt;order&gt;</c>) and the batch <c>resume &lt;order&gt;</c> CLI mode so both accept the same
/// syntax.
/// </summary>
/// <remarks>
/// This parses the user-typed ORDER KEY (<c>GG_SS[_TT]</c>), NOT a filename — deliberately distinct from
/// <see cref="StepLoader.TryParseOrder(string, out StepOrder?, out string?)"/>, whose filename format
/// includes an optional slug segment (<c>GG_SS[_TT][_slug].cs</c>) that is absent here.
/// </remarks>
public static class OrderKeyParser
{
    /// <summary>
    /// Parses a step order key of the form <c>GG_SS</c> or <c>GG_SS_TT</c>. Leading/trailing whitespace is
    /// ignored. Returns <see langword="false"/> on any parse error without throwing.
    /// </summary>
    /// <param name="text">The order key text (e.g. <c>"01_20"</c>).</param>
    /// <param name="order">The parsed order on success; <see langword="null"/> on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a valid order key.</returns>
    public static bool TryParse(string? text, out StepOrder? order)
    {
        order = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();

        var parts = text.Split('_');
        if (parts.Length < 2 || parts.Length > 3)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var group))
            return false;

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var step))
            return false;

        int? subStep = null;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ss))
                return false;
            subStep = ss;
        }

        order = new StepOrder(group, step, subStep, Slug: null);
        return true;
    }
}
