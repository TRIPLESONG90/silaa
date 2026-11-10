namespace silaa.Services;

/// <summary>
/// Cancels an Agent run after a period without streaming activity. Interactive
/// user waits can pause the timer without disabling the caller's cancellation.
/// </summary>
internal sealed class RunActivityTimeout : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _duration;
    private readonly CancellationTokenSource _source;
    private readonly Timer _timer;
    private int _pauseCount;
    private bool _disposed;
    private int _timedOut;

    public RunActivityTimeout(CancellationToken cancellationToken, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        _duration = duration;
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new Timer(OnTimeout, null, duration, Timeout.InfiniteTimeSpan);
    }

    public CancellationToken Token => _source.Token;

    public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

    /// <summary>Starts a fresh inactivity window after a streamed update.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_disposed || _pauseCount > 0 || _source.IsCancellationRequested) return;
            _timer.Change(_duration, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Pauses only the inactivity timer. The external cancellation token remains
    /// linked, so Stop/Exit still cancels immediately while a card is awaiting input.
    /// </summary>
    public IDisposable Pause()
    {
        lock (_gate)
        {
            if (_disposed) return PauseScope.Empty;
            if (_pauseCount++ == 0)
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return new PauseScope(this);
        }
    }

    private void Resume()
    {
        lock (_gate)
        {
            if (_disposed || _pauseCount == 0) return;
            if (--_pauseCount == 0 && !_source.IsCancellationRequested)
                _timer.Change(_duration, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimeout(object? state)
    {
        if (Interlocked.Exchange(ref _timedOut, 1) != 0) return;
        try { _source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            _source.Dispose();
        }
    }

    private sealed class PauseScope(RunActivityTimeout? owner) : IDisposable
    {
        private RunActivityTimeout? _owner = owner;
        public static readonly PauseScope Empty = new(null);

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Resume();
    }
}
