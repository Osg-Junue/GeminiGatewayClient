namespace GeminiAPIWrapper.Http;

/// <summary>
/// Cancels a streaming read when no data arrives within a configured idle period.
/// <para>
/// <see cref="HttpClient.Timeout"/> only covers the time until response headers arrive when using
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so a stalled stream would otherwise hang indefinitely.
/// </para>
/// </summary>
internal sealed class StreamIdleWatchdog : IDisposable
{
    private readonly CancellationTokenSource? _cts;
    private readonly CancellationToken _callerToken;
    private readonly TimeSpan _idleTimeout;
    private volatile bool _reading;

    public StreamIdleWatchdog(TimeSpan? idleTimeout, CancellationToken callerToken)
    {
        _callerToken = callerToken;
        if (idleTimeout is { } timeout && timeout != Timeout.InfiniteTimeSpan)
        {
            _idleTimeout = timeout;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        }
    }

    /// <summary>
    /// The token to pass to read operations (linked to the caller's token).
    /// </summary>
    public CancellationToken Token => _cts?.Token ?? _callerToken;

    /// <summary>
    /// The configured idle timeout, or <c>null</c> when disabled.
    /// </summary>
    public TimeSpan? IdleTimeout => _cts is null ? null : _idleTimeout;

    /// <summary>
    /// <c>true</c> when <see cref="Token"/> was canceled by the idle timer during an active stream read rather than by the caller.
    /// </summary>
    public bool HasTimedOut => _reading && _cts is { IsCancellationRequested: true } && !_callerToken.IsCancellationRequested;

    /// <summary>
    /// Arms the idle timer immediately before awaiting a stream read.
    /// </summary>
    public void BeforeRead()
    {
        if (_cts is { IsCancellationRequested: false })
        {
            _reading = true;
            _cts.CancelAfter(_idleTimeout);
        }
    }

    /// <summary>
    /// Disarms the idle timer immediately after a stream read completes so consumer processing time is never counted as stream idle time.
    /// </summary>
    public void AfterRead()
    {
        _reading = false;
        if (_cts is { IsCancellationRequested: false })
        {
            _cts.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose() => _cts?.Dispose();
}

