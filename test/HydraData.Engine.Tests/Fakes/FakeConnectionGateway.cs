// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IConnectionGateway"/> recording how many slots it opened and which
/// connection ids were requested. Used by the StepRunner / PumpContext tests (no real DB).
/// </summary>
internal sealed class FakeConnectionGateway : IConnectionGateway
{
    private readonly List<FakeDbSlot> _slots = [];
    private readonly Dictionary<string, FakeDbSlot> _slotsById = new(StringComparer.OrdinalIgnoreCase);

    // Signals a waiter as soon as any slot count threshold is reached (used instead of polling).
    private readonly List<(int Count, TaskCompletionSource Tcs)> _waiters = [];
    private readonly object _lock = new();

    /// <summary>All slots opened by this gateway, in open order (one per distinct <see cref="ConnectionInfo.Id"/>).</summary>
    public IReadOnlyList<FakeDbSlot> Slots => _slots;

    /// <summary>The connection ids passed to <see cref="Open"/>, in call order (one per call).</summary>
    public List<string> OpenedIds { get; } = [];

    /// <summary>The command-timeout values passed to <see cref="Open"/>, in call order (one per call).</summary>
    public List<int?> CommandTimeouts { get; } = [];

    /// <summary>
    /// When set, the next slot opened by this gateway will throw from <see cref="FakeDbSlot.Commit"/>.
    /// Cleared after the slot is opened so subsequent slots behave normally.
    /// </summary>
    public bool NextSlotThrowsOnCommit { get; set; }

    /// <summary>
    /// When set, the next slot opened by this gateway will throw from <see cref="FakeDbSlot.Rollback"/>.
    /// Cleared after the slot is opened so subsequent slots behave normally.
    /// </summary>
    public bool NextSlotThrowsOnRollback { get; set; }

    /// <summary>Returns the slot opened for the given connection id, or fails the lookup if none was opened.</summary>
    /// <param name="id">The canonical connection id (<c>targetSystem|name</c>, case-insensitive).</param>
    /// <returns>The recorded slot for that id.</returns>
    public FakeDbSlot SlotFor(string id) => _slotsById[id];

    /// <summary>
    /// Returns a task that completes deterministically when at least <paramref name="count"/> slots
    /// have been opened, or immediately if already satisfied. The task is cancelled via
    /// <paramref name="ct"/> so the test cancellation token propagates correctly.
    /// </summary>
    public Task WaitForSlotCountAsync(int count, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);

        TaskCompletionSource? tcs;
        lock (_lock)
        {
            if (_slots.Count >= count)
                return Task.CompletedTask;

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, tcs));
        }

        // Register outside the lock so the callback does not re-enter the lock and
        // so a pre-cancelled token is handled cleanly (the IsCancellationRequested
        // fast path above prevents leaving a dangling waiter when already cancelled).
        ct.Register(() =>
        {
            lock (_lock) _waiters.RemoveAll(w => ReferenceEquals(w.Tcs, tcs));
            tcs.TrySetCanceled(ct);
        });

        return tcs.Task;
    }

    public IDbSlot Open(ConnectionInfo info, int? commandTimeoutSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        OpenedIds.Add(info.Id);
        CommandTimeouts.Add(commandTimeoutSeconds);
        var slot = new FakeDbSlot
        {
            ThrowOnCommit = NextSlotThrowsOnCommit,
            ThrowOnRollback = NextSlotThrowsOnRollback,
        };
        NextSlotThrowsOnCommit = false;
        NextSlotThrowsOnRollback = false;

        List<TaskCompletionSource>? toSignal = null;
        lock (_lock)
        {
            _slots.Add(slot);
            _slotsById[info.Id] = slot;

            // Signal any waiters whose threshold is now met.
            var count = _slots.Count;
            toSignal = _waiters
                .Where(w => count >= w.Count)
                .Select(w => w.Tcs)
                .ToList();
            _waiters.RemoveAll(w => count >= w.Count);
        }

        foreach (var tcs in toSignal)
            tcs.TrySetResult();

        return slot;
    }
}

/// <summary>Records Commit/Rollback/Dispose calls for transaction-policy assertions.</summary>
internal sealed class FakeDbSlot : IDbSlot
{
    /// <summary>When <see langword="true"/>, <see cref="Commit"/> throws an <see cref="InvalidOperationException"/>.</summary>
    public bool ThrowOnCommit { get; init; }

    /// <summary>When <see langword="true"/>, <see cref="Rollback"/> throws an <see cref="InvalidOperationException"/>.</summary>
    public bool ThrowOnRollback { get; init; }

    /// <summary>Number of times <see cref="Commit"/> was called.</summary>
    public int Commits { get; private set; }

    /// <summary>Number of times <see cref="Rollback"/> was called.</summary>
    public int Rollbacks { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> was called.</summary>
    public bool Disposed { get; private set; }

    /// <summary>The fake executor; touching it marks the slot as accessed.</summary>
    public FakeDbExecutor FakeExecutor { get; } = new();

    /// <summary>Whether the executor was ever accessed (i.e. the step touched the DB).</summary>
    public bool Accessed { get; private set; }

    public IDbExecutor Executor
    {
        get
        {
            Accessed = true;
            return FakeExecutor;
        }
    }

    public void Commit()
    {
        Commits++;
        if (ThrowOnCommit)
            throw new InvalidOperationException("Simulated commit failure.");
    }

    public void Rollback()
    {
        Rollbacks++;
        if (ThrowOnRollback)
            throw new InvalidOperationException("Simulated rollback failure.");
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Minimal executor returning empty results; records the SQL it received.</summary>
internal sealed class FakeDbExecutor : IDbExecutor
{
    /// <summary>The SQL statements passed to any executor method, in order.</summary>
    public List<string> Statements { get; } = [];

    public List<dynamic> Query(string sql, object? param)
    {
        Statements.Add(sql);
        return [];
    }

    public T Scalar<T>(string sql, object? param)
    {
        Statements.Add(sql);
        return default!;
    }

    public int Execute(string sql, object? param)
    {
        Statements.Add(sql);
        return 0;
    }

    public void BulkInsert(string table, IEnumerable<IDictionary<string, object?>> rows) =>
        Statements.Add($"BULK {table}");
}
