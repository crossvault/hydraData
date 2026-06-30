// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Construction-time options for a <see cref="PumpEngine"/> instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wiring (important):</b> <see cref="PumpOptions"/> is <em>not</em> a parameter of
/// <see cref="IPumpEngine.ExecuteAsync"/>. The signature fixed in runtime contract; instead the
/// options are supplied when the engine is constructed and apply to <em>all</em> runs of that instance.
/// A host that needs to vary the folder policy or unsafe grant per run constructs a second engine
/// instance (the documented path). This avoids a second run-parameter path and a second result type.
/// </para>
/// </remarks>
/// <param name="WorkspaceBase">
/// The base directory under which each run's <c>RunDir</c> (<c>WorkspaceBase/&lt;RunId&gt;</c>) is created.
/// </param>
/// <param name="Folders">
/// The read-/write-allowlist policy. A host that does not need folder
/// restrictions passes <see cref="PumpFolderPolicy.Empty"/>; a host reading the policy from
/// <c>appsettings.json</c> passes the loaded policy here.
/// </param>
/// <param name="AllowUnsafeDirectAccess">
/// Safemode opt-out. When <see langword="false"/> (the default), <c>@unsafe</c>
/// steps raise PUMP010 at validate time and <c>Raw</c>/free DuckDB data sources are blocked at run time.
/// </param>
/// <param name="StepTimeout">
/// Optional per-step timeout fed into the existing <see cref="StepRunner"/> timeout
///. <see langword="null"/> disables it.
/// <para>
/// It is also threaded down to the database seam as the Dapper <c>commandTimeout</c> (seconds, rounded
/// up; minimum 1) on every <c>Query</c>/<c>Scalar</c>/<c>Execute</c> and bulk insert, so a long
/// server-side query is bounded by the step timeout. Caller cancellation
/// mid-DB-call is bounded by that command timeout (and the connection's connect timeout), <b>not</b>
/// instantaneous; a purely CPU-bound script loop still requires a process-level kill. When
/// <see langword="null"/>, no command-timeout override is applied — the ADO.NET provider default holds.
/// </para>
/// </param>
/// <param name="LegacyGlobalState">
/// Migration switch: when <see langword="true"/>, all groups share one run-global <see cref="PumpState"/>
/// for <c>State</c> (legacy behaviour). The default (<see langword="false"/>) gives each group its own
/// group-local <c>State</c> while <c>Shared</c> stays run-global.
/// </param>
/// <param name="LegacyGroupBySlug">
/// Migration switch passed through to discovery/loader grouping. The engine
/// itself does not interpret it; it is carried for the Host to wire into the loader.
/// </param>
public sealed record PumpOptions(
    string WorkspaceBase,
    PumpFolderPolicy Folders,
    bool AllowUnsafeDirectAccess = false,
    TimeSpan? StepTimeout = null,
    bool LegacyGlobalState = false,
    bool LegacyGroupBySlug = false);
