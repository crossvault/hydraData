// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T02.4: per script exactly one compilation, cached by the script text (ordinal string key). The observable
/// <see cref="ScriptCompiler.CompileCount"/> stays at 1 for repeated identical text and grows for
/// distinct text.
/// </summary>
public class ScriptCompilerTests
{
    private static async Task<StepResult> Run(ScriptCompiler compiler, string code)
    {
        var runner = compiler.GetRunner(code);
        var ctx = PumpContextFactory.Create(new FakeConnectionGateway());
        return await runner(ctx, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Same_text_run_twice_compiles_once()
    {
        var compiler = new ScriptCompiler();
        const string code = "return Ok(\"hi\");";

        var first = await Run(compiler, code);
        var second = await Run(compiler, code);

        Assert.Equal(1, compiler.CompileCount);
        Assert.Equal("hi", first.Message);
        Assert.Equal("hi", second.Message);
    }

    [Fact]
    public void Different_text_compiles_twice()
    {
        var compiler = new ScriptCompiler();

        compiler.GetRunner("return Ok(\"a\");");
        compiler.GetRunner("return Ok(\"b\");");

        Assert.Equal(2, compiler.CompileCount);
    }

    [Fact]
    public void Compile_error_surfaces_diagnostics()
    {
        var compiler = new ScriptCompiler();

        var ex = Assert.Throws<ScriptCompileException>(() => compiler.GetRunner("return Qery(\"x\");"));

        Assert.NotEmpty(ex.Diagnostics);
        // The cache must not record a failed compilation as a success.
        Assert.Equal(0, compiler.CompileCount);
    }

    [Fact]
    public void Safe_script_cannot_compile_connection_string_access()
    {
        var compiler = new ScriptCompiler();

        var ex = Assert.Throws<ScriptCompileException>(() =>
            compiler.GetRunner("return Ok(CurrentConnection.ConnectionString);"));

        Assert.Contains(ex.Diagnostics, diagnostic =>
            diagnostic.Code == "CS1061" &&
            diagnostic.Message.Contains("ConnectionString", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Warning_producing_script_compiles_and_runs_without_poisoning_cache()
    {
        // A script that produces a Roslyn WARNING but no error must compile successfully: errors==0, so
        // GetRunner returns a working delegate, CompileCount increments, and the runner produces the
        // script's result. Warnings must never throw or be cached as a failure. A '#warning' directive
        // emits CS1030 (Warning) deterministically while the script still executes cleanly (unlike a
        // null-dereference warning, which would compile but throw at run time).
        var compiler = new ScriptCompiler();
        const string code =
            "#warning intentional compile warning\n" +
            "return Ok(\"warned\");";

        var result = await Run(compiler, code);

        Assert.Equal("warned", result.Message);
        Assert.Equal(1, compiler.CompileCount);

        // A second run of the same warning-producing text hits the cache (still exactly one compile),
        // proving the warning did not poison the cache entry.
        var again = await Run(compiler, code);
        Assert.Equal("warned", again.Message);
        Assert.Equal(1, compiler.CompileCount);
    }

    [Fact]
    public async Task Concurrent_requests_for_same_script_compile_exactly_once()
    {
        // Correction 2: ConcurrentDictionary.GetOrAdd may call the factory multiple times under
        // concurrent access; the Lazy<T> wrapper ensures exactly one compile body runs per key.
        var compiler = new ScriptCompiler();
        const string code = "return Ok(\"concurrent\");";
        const int threads = 16;

        // Launch N tasks that all request the same script text simultaneously.
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
        {
            var runner = compiler.GetRunner(code);
            var ctx = PumpContextFactory.Create(new FakeConnectionGateway());
            return await runner(ctx, TestContext.Current.CancellationToken);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        // Compile happened exactly once.
        Assert.Equal(1, compiler.CompileCount);

        // Every task got a working runner producing the correct result.
        Assert.All(results, r => Assert.Equal("concurrent", r.Message));
    }
}
