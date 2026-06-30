// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Script metadata extracted from leading <c>// @tag: value</c> comment lines.
/// </summary>
/// <param name="Name">Display name from <c>// @name</c>, or <see langword="null"/> when absent.</param>
/// <param name="Description">Description from <c>// @description</c>, or <see langword="null"/> when absent.</param>
/// <param name="HaltOnError">
/// Whether the run should halt when this step errors. Defaults to <see langword="true"/>
/// per <c>// @haltOnError</c>; the tag is optional.
/// </param>
/// <param name="Unsafe">
/// Whether the step requires the host's unsafe permission. Defaults to <see langword="false"/>
/// per <c>// @unsafe</c>; the tag is optional.
/// </param>
public sealed record StepMeta(
    string? Name,
    string? Description,
    bool HaltOnError,
    bool Unsafe)
{
    /// <summary>Default metadata when no tags are present.</summary>
    public static StepMeta Default { get; } = new(null, null, HaltOnError: true, Unsafe: false);

    /// <summary>
    /// Parses the leading comment block of a script source for <c>// @tag: value</c> entries.
    /// The <c>@type</c> tag is intentionally ignored (it is a script-level annotation
    /// unrelated to the connection XML attribute — see runtime contract).
    /// </summary>
    public static StepMeta Parse(string scriptSource)
    {
        string? name = null;
        string? description = null;
        bool haltOnError = true;
        bool @unsafe = false;

        foreach (var line in scriptSource.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();

            // Only inspect leading comment lines; stop at first non-comment, non-blank line.
            if (trimmed.IsEmpty) continue;
            if (!trimmed.StartsWith("//", StringComparison.Ordinal)) break;

            var commentContent = trimmed[2..].TrimStart();
            if (!commentContent.StartsWith('@')) continue;

            var colonIdx = commentContent.IndexOf(':');
            if (colonIdx < 0) continue;

            var tag = commentContent[1..colonIdx].Trim().ToString();
            var value = commentContent[(colonIdx + 1)..].Trim().ToString();

            switch (tag.ToLowerInvariant())
            {
                case "name": name = value; break;
                case "description": description = value; break;
                case "haltonerror":
                    haltOnError = ParseBool(value, defaultValue: true); break;
                case "unsafe":
                    @unsafe = ParseBool(value, defaultValue: false); break;
                case "type":
                    // Intentionally ignored — @type is a script-level marker, not the
                    // connection XML attribute. See runtime contract
                    break;
            }
        }

        return new StepMeta(name, description, haltOnError, @unsafe);
    }

    private static bool ParseBool(string value, bool defaultValue) =>
        value.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => defaultValue,
        };
}
