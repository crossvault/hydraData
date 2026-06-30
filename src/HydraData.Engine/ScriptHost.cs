// Copyright (c) 2026 crossVault GmbH.

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;

namespace HydraData.Engine;

/// <summary>
/// Single source of truth for script compilation: the immutable <see cref="ScriptOptions"/> shared
/// by validation and execution. Holds the metadata references and the
/// imports — including <c>using static HydraData.Engine.Fn</c> — so scripts call <see cref="Fn"/>
/// helpers and use the common namespaces without their own <c>using</c> directives.
/// </summary>
/// <remarks>
/// References are gathered from the loaded assemblies via <see cref="Assembly.Location"/> plus the
/// runtime's trusted-platform-assemblies list (no extra NuGet package). Runtime NuGet resolution
/// (<c>#r "nuget:"</c>) is deliberately NOT enabled; PUMP001 detection is a validate-side concern.
/// </remarks>
public static class ScriptHost
{
    /// <summary>
    /// The shared, immutable compile options. The globals type is <see cref="PumpContext"/>;
    /// the options carry the metadata references and imports.
    /// </summary>
    public static ScriptOptions Options { get; } = BuildOptions();

    private static ScriptOptions BuildOptions() =>
        ScriptOptions.Default
            .WithReferences(BuildReferences())
            .WithImports(
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.Text",
                "System.Threading",
                "System.Threading.Tasks",
                "HydraData.Engine")
            .AddImports("HydraData.Engine.Fn"); // using static Fn — bare helper calls (iif, coalesce, ...).

    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddByLocation(string? location)
        {
            if (string.IsNullOrEmpty(location)) return; // in-memory / single-file assemblies have no location
            if (!seen.Add(location)) return;
            references.Add(MetadataReference.CreateFromFile(location));
        }

        // The runtime's trusted-platform-assemblies list covers System.*, netstandard, etc.
        // Known limitation: under a single-file / trimmed publish host, typeof(...).Assembly.Location
        // is empty and the TPA list may be incomplete or absent. In that scenario a fallback such as
        // Basic.Reference.Assemblies would be needed to supply the framework metadata references. The
        // current normal and test host is a standard non-trimmed process where TPA is fully populated.
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            AddByLocation(path);

        // The Engine assembly itself (PumpContext, Fn, StepResult, ...) — typically already in TPA
        // for the test/host process, but added explicitly so a privately-deployed Engine still resolves.
        AddByLocation(typeof(ScriptHost).Assembly.Location);

        return references.ToImmutableArray();
    }
}
