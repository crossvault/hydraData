// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Merges step scripts from one or more script folders into an immutable
/// <see cref="ScriptContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// During discovery: each folder is loaded via the existing <see cref="StepLoader"/>; no
/// parsing or sort rules are duplicated here. The file paths from all folders are
/// collected in folder order and then passed to <see cref="StepLoader.LoadFiles"/>
/// for a single global sort and post-sort validation pass.
/// </para>
/// <para>
/// Groups (GG) are global across all folders — a group may span multiple folders.
/// The cross-folder pass preserves all loader invariants including
/// <see cref="LoaderWarningKind.DuplicateOrder"/> and
/// <see cref="LoaderWarningKind.NonContiguousGroup"/> (including in
/// <c>LegacyGroupBySlug</c> mode).
/// </para>
/// <para>
/// <b>Sort stability note:</b> <c>List&lt;T&gt;.Sort</c> is not a stable sort. When two
/// steps share the same numeric order (a <see cref="LoaderWarningKind.DuplicateOrder"/>
/// collision), their relative position in the output is unspecified — folder order is
/// not a guaranteed tiebreak.
/// </para>
/// </remarks>
public sealed class DiscoveryService
{
    private readonly StepLoader _loader;

    /// <summary>
    /// Initialises a new <see cref="DiscoveryService"/> using the supplied loader options.
    /// </summary>
    /// <param name="options">Loader options; <see langword="null"/> uses defaults (new schema).</param>
    public DiscoveryService(LoaderOptions? options = null) =>
        _loader = new StepLoader(options);

    /// <summary>
    /// Discovers all steps in <paramref name="scriptFolders"/>, merges them in folder order,
    /// and returns an immutable <see cref="ScriptContext"/>.
    /// </summary>
    /// <param name="scriptFolders">
    /// Ordered list of directories to scan. Each directory is scanned non-recursively for
    /// <c>.cs</c> files using the existing <see cref="StepLoader"/>. The folder order
    /// determines the merge priority; all loader parsing, sorting, and warning rules apply
    /// globally across all provided folders.
    /// </param>
    /// <returns>An immutable <see cref="ScriptContext"/> ready for validation and execution.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// Thrown when any entry in <paramref name="scriptFolders"/> does not exist on disk.
    /// A missing script folder is a configuration error; the engine fails fast so the
    /// caller can correct the path rather than silently producing an empty context.
    /// The exception message includes the missing folder path.
    /// </exception>
    public ScriptContext Discover(IReadOnlyList<string> scriptFolders)
    {
        ArgumentNullException.ThrowIfNull(scriptFolders);

        // Collect every .cs file path from each folder in order.
        // GatherCsFiles is the canonical glob (defined on StepLoader) — one place for the
        // pattern. The single global LoadFiles call below handles all parsing, meta-reading,
        // sorting, and warning detection.
        var allFilePaths = new List<string>();
        var folderComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seenFolders = new HashSet<string>(folderComparer);

        foreach (var folder in scriptFolders)
        {
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException(
                    $"Script folder does not exist: '{folder}'. " +
                    "Verify the path in your engine configuration.");

            var normalizedFolder = NormalizeFolder(folder);
            if (!seenFolders.Add(normalizedFolder))
                continue;

            allFilePaths.AddRange(StepLoader.GatherCsFiles(normalizedFolder));
        }

        // Single pass: StepLoader sorts globally, reads meta, detects all warnings
        // (DuplicateOrder, NonContiguousGroup, InvalidTag, SlugInconsistency) across the
        // merged file list. LegacyGroupBySlug is fully respected because the same loader
        // instance is used.
        var merged = _loader.LoadFiles(allFilePaths);

        // Build sorted distinct group list from the globally sorted steps.
        var groups = merged.Steps
            .Select(s => s.Order.Group)
            .Distinct()
            .OrderBy(g => g)
            .ToList();

        return new ScriptContext(
            groups: groups.AsReadOnly(),
            steps: merged.Steps,
            warnings: merged.Warnings);
    }

    private static string NormalizeFolder(string folder)
    {
        var fullPath = Path.GetFullPath(folder);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.Ordinal)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
