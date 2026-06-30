// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Single source of truth for engine-owned diagnostic codes.
/// </summary>
/// <remarks>
/// <para>
/// Engine-owned diagnostic codes (PUMP-prefix). CS-codes originate from Roslyn and are not
/// listed here. Adding a new code requires one <c>const string</c> here and one row in the
/// PUMP-Diagnose-Katalog table — no extension framework needed (YAGNI).
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Code</term><description>Meaning</description>
///   </listheader>
///   <item>
///     <term><see cref="CompileSetupGuard"/></term>
///     <description>INTERNAL — compilation setup threw unexpectedly (host misconfiguration); never surfaces without a definition.</description>
///   </item>
///   <item>
///     <term><see cref="NuGetDirective"/></term>
///     <description><c>#r "nuget:"</c> directive found in script text; runtime NuGet resolution is not supported.</description>
///   </item>
///   <item>
///     <term><see cref="UnsafeWithoutGrant"/></term>
///     <description><c>@unsafe: true</c> meta set on the step but the engine was not created with <c>AllowUnsafeDirectAccess = true</c>.</description>
///   </item>
///   <item>
///     <term><see cref="MissingConnection"/></term>
///     <description>A referenced connection could not be resolved during preflight.</description>
///   </item>
/// </list>
/// </remarks>
public static class PumpDiagnostics
{
    /// <summary>
    /// PUMP000 — INTERNAL guard: the compilation setup itself threw an unexpected exception
    /// (e.g. a misconfigured host or missing Roslyn assemblies). This code is never emitted
    /// during normal script compilation; it exists solely so every code that can surface in
    /// <see cref="ValidationReport"/> has a named constant here. Not listed in the public
    /// PUMP-Diagnose-Katalog because it indicates a host misconfiguration, not a script error.
    /// </summary>
    internal const string CompileSetupGuard = "PUMP000";

    /// <summary>
    /// PUMP001 — <c>#r "nuget:"</c> directive is present in the script text.
    /// Runtime NuGet resolution is deliberately not supported.
    /// </summary>
    public const string NuGetDirective = "PUMP001";

    /// <summary>
    /// PUMP010 — <c>@unsafe: true</c> meta is set on the step but the engine has not granted
    /// <c>AllowUnsafeDirectAccess</c>. Both the script meta and the engine flag are required
    ///.
    /// </summary>
    public const string UnsafeWithoutGrant = "PUMP010";

    /// <summary>
    /// PUMP020 — a referenced connection could not be resolved during preflight (e.g. the connection
    /// directory has no <c>Default</c> while there are steps to run). A preflight failure aborts the run
    /// before any step executes.
    /// </summary>
    public const string MissingConnection = "PUMP020";
}
