// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Host;

/// <summary>
/// The strongly-typed <c>Pump</c> configuration section, bound from
/// <c>appsettings.json</c>. Plain mutable properties so <c>IConfiguration.Bind</c> can populate them; the
/// host maps this to the engine's immutable <c>PumpOptions</c> via <see cref="PumpOptionsMapper"/>.
/// </summary>
public sealed class PumpSettings
{
    /// <summary>The configuration section name (<c>Pump</c>).</summary>
    public const string SectionName = "Pump";

    /// <summary>Base directory under which each run's <c>RunDir</c> is created. Default <c>./_runs</c>.</summary>
    public string WorkspaceBase { get; set; } = "./_runs";

    /// <summary>Safemode opt-out. Default <see langword="false"/>.</summary>
    public bool AllowUnsafeDirectAccess { get; set; }

    /// <summary>Folders scripts may read from (relative paths are resolved against the base directory).</summary>
    public IList<string> ReadAllowlist { get; set; } = [];

    /// <summary>Folders scripts may write to (relative paths are resolved against the base directory).</summary>
    public IList<string> WriteAllowlist { get; set; } = [];

    /// <summary>Per-step timeout in seconds; <c>0</c> or negative disables it.</summary>
    public int StepTimeoutSeconds { get; set; } = 120;

    /// <summary>Host-side retention: delete <c>RunDir</c> folders older than this many days. <c>0</c> disables.</summary>
    public int RunDirRetentionDays { get; set; } = 14;

    /// <summary>Migration switch: one run-global <c>State</c> for all groups.</summary>
    public bool LegacyGlobalState { get; set; }

    /// <summary>Migration switch: slug-based grouping in discovery.</summary>
    public bool LegacyGroupBySlug { get; set; }

    /// <summary>Ordered script folders scanned by discovery (relative paths resolved against the base directory).</summary>
    public IList<string> ScriptFolders { get; set; } = [];

    /// <summary>Path to <c>connections.xml</c> (relative paths resolved against the base directory).</summary>
    public string ConnectionsFile { get; set; } = "./connections.xml";
}
