// Copyright (c) 2026 crossVault GmbH.

using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T02.5: the capture serialises (a second capture waits for the first) and always restores the
/// original console writers, even when the step body throws.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public class StepOutputCaptureTests
{
    [Fact]
    public async Task Captures_stdout_and_stderr()
    {
        var original = Console.Out;

        await using (var capture = await StepOutputCapture.StartAsync(TestContext.Current.CancellationToken))
        {
            Console.WriteLine("out-line");
            Console.Error.WriteLine("err-line");
            var text = capture.Output;
            Assert.Contains("out-line", text, StringComparison.Ordinal);
            Assert.Contains("err-line", text, StringComparison.Ordinal);
        }

        Assert.Same(original, Console.Out);
    }

    [Fact]
    public async Task Console_is_restored_even_when_body_throws()
    {
        var original = Console.Out;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var capture = await StepOutputCapture.StartAsync(TestContext.Current.CancellationToken);
            Console.WriteLine("before throw");
            throw new InvalidOperationException("boom");
        });

        Assert.Same(original, Console.Out);
    }

    [Fact]
    public async Task Second_capture_waits_for_the_first()
    {
        await using var first = await StepOutputCapture.StartAsync(TestContext.Current.CancellationToken);

        // While the first holds the gate, a second StartAsync must not complete.
        var secondStart = StepOutputCapture.StartAsync(TestContext.Current.CancellationToken);
        var completedEarly = await Task.WhenAny(secondStart, Task.Delay(100, TestContext.Current.CancellationToken)) == secondStart;
        Assert.False(completedEarly, "second capture acquired the gate while the first was active");

        // Releasing the first lets the second proceed.
        await first.DisposeAsync();
        var second = await secondStart;
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Output_read_uses_the_synchronized_writer_monitor()
    {
        await using var capture = await StepOutputCapture.StartAsync(TestContext.Current.CancellationToken);
        var writerField = typeof(StepOutputCapture).GetField(
            "_writer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var writer = Assert.IsAssignableFrom<TextWriter>(writerField?.GetValue(capture));
        var monitorHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMonitor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = Task.Run(() =>
        {
            lock (writer)
            {
                monitorHeld.SetResult();
                releaseMonitor.Task.GetAwaiter().GetResult();
            }
        }, TestContext.Current.CancellationToken);

        await monitorHeld.Task;
        var readTask = Task.Run(() => capture.Output, TestContext.Current.CancellationToken);

        try
        {
            var completedEarly = await Task.WhenAny(
                readTask,
                Task.Delay(100, TestContext.Current.CancellationToken)) == readTask;
            Assert.False(completedEarly, "Output read did not synchronize with writer operations");
        }
        finally
        {
            releaseMonitor.SetResult();
        }

        await holder;
        await readTask;
    }

    [Fact]
    public async Task Cancelled_gate_wait_does_not_redirect_console_or_leak_gate()
    {
        var original = Console.Out;
        await using var first = await StepOutputCapture.StartAsync(TestContext.Current.CancellationToken);
        var redirected = Console.Out;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StepOutputCapture.StartAsync(cancelled.Token));
        Assert.Same(redirected, Console.Out);

        await first.DisposeAsync();
        Assert.Same(original, Console.Out);

        await using var afterCancellation =
            await StepOutputCapture.StartAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// Serialises the console-capture tests against each other and against the integration test, since
/// they all mutate the process-global <see cref="Console.Out"/>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConsoleCaptureCollection
{
    /// <summary>The xUnit collection name.</summary>
    public const string Name = "console-capture";
}
