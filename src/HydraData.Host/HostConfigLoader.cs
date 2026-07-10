// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HydraData.Host;

/// <summary>
/// Shared configuration-loading helper used by both <see cref="HostBootstrap"/> (full-run) and
/// <see cref="SessionBootstrap"/> (session). Reads <c>appsettings.json</c>, binds
/// <see cref="PumpSettings"/>, and resolves all derived values so callers never duplicate this block.
/// </summary>
internal static class HostConfigLoader
{
    /// <summary>
    /// Loads and resolves all configuration from <paramref name="baseDirectory"/>.
    /// </summary>
    /// <param name="baseDirectory">
    /// The directory that contains <c>appsettings.json</c> (same semantics as <c>HostBootstrap</c>).
    /// </param>
    /// <returns>
    /// A <see cref="HostConfig"/> record holding the bound <see cref="PumpSettings"/>, resolved
    /// <see cref="PumpOptions"/>, script folders, the parsed <see cref="ConnectionRegistry"/>, and its
    /// non-fatal warnings.
    /// </returns>
    /// <remarks>
    /// Configuration loading intentionally occurs here rather than inside each bootstrap's try block
    /// so that any <see cref="FileNotFoundException"/>, <see cref="System.Text.Json.JsonException"/>,
    /// or related error surfaces as the same exception type regardless of whether it is the full-run
    /// or session bootstrap that calls this method.
    /// </remarks>
    internal static HostConfig Load(string baseDirectory, ILogger? logger = null)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var settings = new PumpSettings();
        configuration.GetSection(PumpSettings.SectionName).Bind(settings);

        var options = PumpOptionsMapper.ToPumpOptions(settings, baseDirectory);
        var scriptFolders = PumpOptionsMapper.ResolveScriptFolders(settings, baseDirectory);
        var connectionsFile = PumpOptionsMapper.ResolveConnectionsFile(settings, baseDirectory);

        var registry = ConnectionRegistry.Load(connectionsFile, logger);

        return new HostConfig(settings, options, scriptFolders, registry, registry.Warnings);
    }
}

/// <summary>
/// Immutable result of <see cref="HostConfigLoader.Load"/>: all values derived from
/// <c>appsettings.json</c> that both bootstraps need before starting their respective run paths.
/// </summary>
/// <param name="Settings">The raw bound <see cref="PumpSettings"/> (needed by <see cref="HostBootstrap"/> for retention days).</param>
/// <param name="Options">The resolved engine <see cref="PumpOptions"/> (all paths absolute).</param>
/// <param name="ScriptFolders">Resolved, absolute script folder paths.</param>
/// <param name="Registry">The parsed connection registry; directory materialisation is deferred to the host.</param>
/// <param name="Warnings">Non-fatal warnings produced while parsing <c>connections.xml</c>.</param>
internal sealed record HostConfig(
    PumpSettings Settings,
    PumpOptions Options,
    IReadOnlyList<string> ScriptFolders,
    ConnectionRegistry Registry,
    IReadOnlyList<ConnectionRegistryWarning> Warnings);
