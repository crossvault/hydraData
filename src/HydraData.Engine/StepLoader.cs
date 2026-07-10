// Copyright (c) 2026 crossVault GmbH.

using System.Globalization;

namespace HydraData.Engine;

/// <summary>
/// Discovers, parses, and sorts C# step script files in a directory.
/// </summary>
/// <remarks>
/// <para>
/// Filename format: <c>&lt;GG&gt;_&lt;SS&gt;[_&lt;TT&gt;][_[slug]]_description.cs</c>
/// </para>
/// <para>
/// The parser reads leading underscore-separated purely numeric segments. A bracketed
/// slug is recognised only directly after GG, SS, and the optional TT segment; its contents
/// may contain separators. Once a non-numeric description token begins, later brackets remain
/// part of the description. A period (<c>.</c>) separator is also recognised for compatibility
/// but is not recommended for new scripts.
/// </para>
/// </remarks>
public sealed class StepLoader
{
    private readonly LoaderOptions _options;

    /// <summary>Initialises a new loader with the supplied options.</summary>
    public StepLoader(LoaderOptions? options = null) =>
        _options = options ?? new LoaderOptions();

    /// <summary>
    /// Discovers all <c>.cs</c> files in <paramref name="directory"/>,
    /// parses their names, sorts them, and returns a <see cref="LoadResult"/>.
    /// </summary>
    /// <param name="directory">Directory to scan (non-recursive).</param>
    public LoadResult Load(string directory)
    {
        var files = GatherCsFiles(directory);
        return LoadFiles(files);
    }

    /// <summary>
    /// Returns all <c>.cs</c> files in <paramref name="directory"/> (non-recursive).
    /// This is the single canonical glob used by both <see cref="Load"/> and
    /// <see cref="DiscoveryService"/> so the pattern is never duplicated.
    /// </summary>
    /// <param name="directory">Directory to scan.</param>
    internal static string[] GatherCsFiles(string directory) =>
        Directory.GetFiles(directory, "*.cs", SearchOption.TopDirectoryOnly);

    /// <summary>
    /// Parses and sorts the supplied file paths directly.
    /// Useful for testing without touching the file system.
    /// </summary>
    public LoadResult LoadFiles(IEnumerable<string> filePaths)
    {
        var warnings = new List<LoaderWarning>();
        var descriptors = new List<StepDescriptor>();

        foreach (var path in filePaths)
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var parsed = TryParseOrder(fileName, out var order, out var parseWarning);

            // Always collect parse-time warnings (e.g. InvalidTag) regardless of whether
            // the overall parse succeeded with a partial order.
            if (parseWarning is not null) warnings.Add(parseWarning);

            if (!parsed) continue;

            var meta = ReadMetaOrDefault(path);

            descriptors.Add(new StepDescriptor(
                FileName: Path.GetFileName(path),
                FilePath: path,
                Order: order,
                Meta: meta));
        }

        // Sort segment-wise numerically: GG, SS, TT
        descriptors.Sort((a, b) => a.Order.CompareTo(b.Order));

        // Post-sort warnings
        DetectDuplicateOrders(descriptors, warnings);
        DetectNonContiguousGroups(descriptors, warnings);
        DetectSlugInconsistencies(descriptors, warnings);

        return new LoadResult(descriptors, warnings);
    }

    // ── parsing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to parse the leading numeric segments of a step filename stem
    /// (without extension).
    /// </summary>
    /// <param name="stem">The filename without extension.</param>
    /// <param name="order">The parsed <see cref="StepOrder"/> when the method returns <see langword="true"/>.</param>
    /// <param name="warning">
    /// A <see cref="LoaderWarning"/> when a parse-time issue is detected (e.g. unclosed bracket),
    /// regardless of the return value.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when at least GG and SS could be parsed; otherwise <see langword="false"/>.
    /// </returns>
    internal static bool TryParseOrder(
        string stem,
        out StepOrder order,
        out LoaderWarning? warning)
    {
        order = null!;
        warning = null;

        // Extract a bracketed slug before separator tokenization so a slug may itself contain '_' or '.'.
        // The bracket is a slug boundary only when it directly follows the complete numeric prefix;
        // brackets appearing after a description token remain part of the description.
        var numericPrefix = stem;
        var nums = new List<int>();
        string? slug = null;
        var openBracket = stem.IndexOf('[');
        if (openBracket >= 0 && IsDirectSlugPosition(stem, openBracket))
        {
            numericPrefix = stem[..openBracket];
            var closeBracket = stem.IndexOf(']', openBracket + 1);
            if (closeBracket < 0)
            {
                var invalidSegment = stem[openBracket..];
                warning = new LoaderWarning(
                    LoaderWarningKind.InvalidTag,
                    $"Filename segment '{invalidSegment}' has an opening '[' but no valid closing ']'.");
            }
            else
            {
                slug = stem[openBracket..(closeBracket + 1)];
            }
        }

        // Accept both '_' and '.' as separators for the numeric prefix (compatibility).
        var tokens = numericPrefix.Split(new[] { '_', '.' });

        foreach (var token in tokens)
        {
            if (token.Length == 0) continue;

            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                if (nums.Count < 3)
                    nums.Add(n);
                // more than 3 numeric segments → treat extras as description start; stop
                else break;
            }
            else
            {
                // Non-numeric, non-bracket → description start
                break;
            }
        }

        if (nums.Count < 2)
        {
            // Not a valid step filename — skip silently (may be a utility file)
            return false;
        }

        order = new StepOrder(
            Group: nums[0],
            Step: nums[1],
            SubStep: nums.Count >= 3 ? nums[2] : null,
            Slug: slug);

        return true;
    }

    private static bool IsDirectSlugPosition(string stem, int openBracket)
    {
        if (openBracket == 0 || !IsSeparator(stem[openBracket - 1]))
            return false;

        var prefixTokens = stem[..(openBracket - 1)].Split(new[] { '_', '.' });
        var numericCount = 0;
        foreach (var token in prefixTokens)
        {
            if (token.Length == 0)
                continue;

            if (numericCount >= 3
                || !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }

            numericCount++;
        }

        return numericCount >= 2;
    }

    private static bool IsSeparator(char value) =>
        value is '_' or '.';

    private static StepMeta ReadMetaOrDefault(string path)
    {
        if (!Path.Exists(path))
            return StepMeta.Default;

        try
        {
            return StepMeta.Parse(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return StepMeta.Default;
        }
        catch (UnauthorizedAccessException)
        {
            return StepMeta.Default;
        }
    }

    // ── post-sort validation ─────────────────────────────────────────────────

    private static void DetectDuplicateOrders(
        IReadOnlyList<StepDescriptor> sorted,
        List<LoaderWarning> warnings)
    {
        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = sorted[i - 1];
            var curr = sorted[i];
            if (prev.Order.CompareTo(curr.Order) == 0)
            {
                warnings.Add(new LoaderWarning(
                    LoaderWarningKind.DuplicateOrder,
                    $"Duplicate order: '{prev.FileName}' and '{curr.FileName}' resolve to " +
                    $"the same order ({prev.Order.Group},{prev.Order.Step},{prev.Order.SubStep})."));
            }
        }
    }

    private void DetectNonContiguousGroups(
        IReadOnlyList<StepDescriptor> sorted,
        List<LoaderWarning> warnings)
    {
        // In the GG (new) schema, groups are contiguous-by-construction after the
        // segment-wise numeric sort, so this check is a safety net for two specific cases:
        //   1. The cross-folder merge case (M5 DiscoveryService, T09.1), where files from
        //      multiple directories are merged and re-sorted, potentially interleaving groups.
        //   2. The LegacyGroupBySlug case, where the group key is the slug string and the
        //      numeric GG ordering may cause slug-identified groups to be non-contiguous.
        // For single-folder new-schema usage this warning cannot fire in practice.

        // Track which group keys we have already seen and closed
        var groupKey = _options.LegacyGroupBySlug
            ? (Func<StepDescriptor, string>)(d => d.Order.Slug ?? string.Empty)
            : (d => d.Order.Group.ToString());

        var seen = new Dictionary<string, int>(); // groupKey → index of last occurrence
        var reported = new HashSet<string>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var key = groupKey(sorted[i]);

            if (seen.TryGetValue(key, out var lastIdx) && lastIdx < i - 1)
            {
                // There are entries between lastIdx and i — check if any belong to a different group
                bool hasGap = false;
                for (int j = lastIdx + 1; j < i; j++)
                {
                    if (groupKey(sorted[j]) != key)
                    {
                        hasGap = true;
                        break;
                    }
                }

                if (hasGap && reported.Add(key))
                {
                    warnings.Add(new LoaderWarning(
                        LoaderWarningKind.NonContiguousGroup,
                        $"Non-contiguous group: group '{key}' appears in '{sorted[lastIdx].FileName}' " +
                        $"and again in '{sorted[i].FileName}' with steps from another group in between."));
                }
            }

            seen[key] = i;
        }
    }

    private void DetectSlugInconsistencies(
        IReadOnlyList<StepDescriptor> sorted,
        List<LoaderWarning> warnings)
    {
        if (_options.LegacyGroupBySlug) return; // slug is the group key in legacy mode; no inconsistency concept

        // Group by GG and check that all have the same slug (null counts as a distinct value)
        var groupSlugs = new Dictionary<int, (string? Slug, string FirstFile)>();
        var reported = new HashSet<int>();

        foreach (var d in sorted)
        {
            if (!groupSlugs.TryGetValue(d.Order.Group, out var first))
            {
                groupSlugs[d.Order.Group] = (d.Order.Slug, d.FileName);
            }
            else if (first.Slug != d.Order.Slug && reported.Add(d.Order.Group))
            {
                warnings.Add(new LoaderWarning(
                    LoaderWarningKind.SlugInconsistency,
                    $"Slug inconsistency in group {d.Order.Group}: " +
                    $"'{first.FirstFile}' has slug '{first.Slug}' but '{d.FileName}' has slug '{d.Order.Slug}'."));
            }
        }
    }
}
