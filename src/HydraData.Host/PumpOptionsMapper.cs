// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;

namespace HydraData.Host;

/// <summary>
/// Maps the bound <see cref="PumpSettings"/> to the engine's immutable <see cref="PumpOptions"/> and resolves
/// the configured paths against a base directory. Relative paths in the configuration become absolute,
/// normalised paths.
/// </summary>
public static class PumpOptionsMapper
{
    /// <summary>
    /// Builds <see cref="PumpOptions"/> from <paramref name="settings"/>, resolving relative paths against
    /// <paramref name="baseDirectory"/>.
    /// </summary>
    /// <param name="settings">The bound configuration section.</param>
    /// <param name="baseDirectory">
    /// The directory relative paths are resolved against (typically the host's content root / working
    /// directory). Must be absolute.
    /// </param>
    /// <returns>The engine options with absolute allowlists and a <see cref="TimeSpan"/> step timeout.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public static PumpOptions ToPumpOptions(PumpSettings settings, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var workspaceBase = ResolvePath(settings.WorkspaceBase, baseDirectory);

        var folders = new PumpFolderPolicy(
            ReadAllowlist: ResolveAll(settings.ReadAllowlist, baseDirectory),
            WriteAllowlist: ResolveAll(settings.WriteAllowlist, baseDirectory));

        // StepTimeoutSeconds <= 0 means "no timeout" (null), matching PumpOptions.StepTimeout semantics.
        var stepTimeout = settings.StepTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(settings.StepTimeoutSeconds)
            : (TimeSpan?)null;

        return new PumpOptions(
            WorkspaceBase: workspaceBase,
            Folders: folders,
            AllowUnsafeDirectAccess: settings.AllowUnsafeDirectAccess,
            StepTimeout: stepTimeout,
            LegacyGlobalState: settings.LegacyGlobalState,
            LegacyGroupBySlug: settings.LegacyGroupBySlug);
    }

    /// <summary>Resolves the configured script folders to absolute, normalised paths, in order.</summary>
    public static IReadOnlyList<string> ResolveScriptFolders(PumpSettings settings, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return ResolveAll(settings.ScriptFolders, baseDirectory);
    }

    /// <summary>Resolves the <c>connections.xml</c> path to an absolute, normalised path.</summary>
    public static string ResolveConnectionsFile(PumpSettings settings, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return ResolvePath(settings.ConnectionsFile, baseDirectory);
    }

    private static IReadOnlyList<string> ResolveAll(IEnumerable<string> paths, string baseDirectory) =>
        paths.Select(p => ResolvePath(p, baseDirectory)).ToList();

    private static string ResolvePath(string path, string baseDirectory) =>
        // Path.GetFullPath collapses '.'/'..' and makes the path absolute against the base directory; a path
        // that is already rooted is returned normalised and unchanged.
        Path.GetFullPath(path, baseDirectory);
}
