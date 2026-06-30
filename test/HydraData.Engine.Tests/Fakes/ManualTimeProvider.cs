// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine.Tests.Fakes;

/// <summary>
/// Minimal manually-advanced <see cref="TimeProvider"/> for deterministic timeout tests. Timers only
/// fire when <see cref="Advance"/> moves the clock past their due time; nothing fires on wall-clock
/// time. This makes the per-step timeout in <see cref="StepRunner"/> deterministic (T02.6).
/// </summary>
/// <remarks>
/// A purpose-built fake is used because <c>Microsoft.Extensions.TimeProvider.Testing</c> is not a
/// referenced package; the shared <c>FakeTimeProvider</c> arrives with T08.2. This stays test-local.
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <summary>Advances the clock and fires any timers whose due time is now reached.</summary>
    /// <param name="delta">The amount to advance.</param>
    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += delta;
            due = _timers.Where(t => t.IsDue(_now)).ToList();
        }

        // Fire outside the lock so a callback that disposes/creates timers does not deadlock.
        foreach (var timer in due)
            timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_gate)
        {
            timer.Schedule(_now, dueTime);
            _timers.Add(timer);
        }
        return timer;
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate) _timers.Remove(timer);
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _dueAt;
        private TimeSpan _period;
        private bool _disposed;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _period = period;
        }

        public void Schedule(DateTimeOffset now, TimeSpan dueTime)
        {
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
        }

        public bool IsDue(DateTimeOffset now) => _dueAt is { } at && now >= at;

        public void Fire()
        {
            if (_disposed) return;

            // One-shot for our use; clear or reschedule by period.
            if (_period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero)
                _dueAt = null;
            else
                _dueAt = _owner.GetUtcNow() + _period;

            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
