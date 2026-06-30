// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T02.3: a compiled script sees <see cref="Fn"/> helpers and the common namespaces without its own
/// <c>using</c> directives, because <see cref="ScriptHost.Options"/> carries the references and imports.
/// </summary>
public class ScriptHostTests
{
    private static PumpContext NewContext() =>
        PumpContextFactory.Create(new FakeConnectionGateway());

    [Fact]
    public async Task Script_can_call_Fn_helpers_without_usings()
    {
        // iif and coalesce come from `using static HydraData.Engine.Fn`; no using in the script.
        var script = CSharpScript.Create<StepResult>(
            "var x = iif(true, \"a\", \"b\"); var y = coalesce<string>(null, \"z\"); return Ok(x + y);",
            ScriptHost.Options,
            typeof(PumpContext));

        var result = await script.RunAsync(NewContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Severity.Success, result.ReturnValue.Severity);
        Assert.Equal("az", result.ReturnValue.Message);
    }

    [Fact]
    public async Task Script_can_use_linq_and_collections_without_usings()
    {
        // System.Linq + System.Collections.Generic are imported by ScriptHost.
        var script = CSharpScript.Create<StepResult>(
            "var list = new List<int> { 3, 1, 2 }; var n = list.OrderBy(i => i).First(); return Ok(n.ToString());",
            ScriptHost.Options,
            typeof(PumpContext));

        var result = await script.RunAsync(NewContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("1", result.ReturnValue.Message);
    }
}
