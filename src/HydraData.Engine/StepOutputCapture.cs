// Copyright (c) 2026 crossVault GmbH.

using System.Text;

namespace HydraData.Engine;

/// <summary>
/// Captures a step's <see cref="Console.Out"/> and <see cref="Console.Error"/> for the duration of
/// its execution. Steps run sequentially, so a process-wide
/// <see cref="SemaphoreSlim"/> serialises captures: a second capture waits until the first is
/// disposed. The original console writers are always restored in <see cref="DisposeAsync"/>, even when
/// the step body throws.
/// </summary>
public sealed class StepOutputCapture : IAsyncDisposable
{
    // Process-global: console redirection is process-wide, so only one capture may be active at a time.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly StringBuilder _buffer = new();
    private readonly TextWriter _writer;
    private bool _disposed;

    private StepOutputCapture(TextWriter originalOut, TextWriter originalError)
    {
        _originalOut = originalOut;
        _originalError = originalError;
        // Synchronized wrapper around a StringWriter — thread-safe writes into a shared StringBuilder.
        _writer = TextWriter.Synchronized(new StringWriter(_buffer));
    }

    /// <summary>
    /// Acquires the capture gate (waiting if another capture is active) and redirects
    /// <see cref="Console.Out"/>/<see cref="Console.Error"/> to a shared in-memory writer.
    /// </summary>
    /// <param name="ct">Cancellation token observed while waiting for the gate.</param>
    /// <returns>An active capture; dispose it to restore the console and release the gate.</returns>
    public static async Task<StepOutputCapture> StartAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);

        var capture = new StepOutputCapture(Console.Out, Console.Error);
        try
        {
            Console.SetOut(capture._writer);
            Console.SetError(capture._writer);
        }
        catch
        {
            Gate.Release();
            throw;
        }

        return capture;
    }

    /// <summary>The text written to stdout and stderr during the capture, in write order.</summary>
    public string Output
    {
        get
        {
            lock (_writer) return _buffer.ToString();
        }
    }

    /// <summary>Restores the original console writers and releases the capture gate. Idempotent.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        try
        {
            Console.SetOut(_originalOut);
            Console.SetError(_originalError);
            _writer.Dispose();
        }
        finally
        {
            Gate.Release();
        }

        return ValueTask.CompletedTask;
    }
}
