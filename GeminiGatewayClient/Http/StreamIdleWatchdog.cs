using System.Diagnostics;

namespace GeminiGatewayClient.Http;

/// <summary>
/// Cancels a streaming read when no data arrives within a configured idle period.
/// <para>
/// <see cref="HttpClient.Timeout"/> only covers the time until response headers arrive when using
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so a stalled stream would otherwise hang indefinitely.
/// </para>
/// </summary>
internal sealed class StreamIdleWatchdog : IDisposable
{
    private static readonly TimeSpan s_timerResolutionGuard = TimeSpan.FromMilliseconds(50);

    private CancellationTokenSource? _cts;
    private readonly CancellationToken _callerToken;
    private readonly TimeSpan _idleTimeout;
    private long _readStartTimestamp;
    private volatile bool _reading;
    private volatile bool _timedOut;

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
    /// The caller's original cancellation token (never cancelled by the idle timer).
    /// </summary>
    public CancellationToken CallerToken => _callerToken;

    /// <summary>
    /// The configured idle timeout, or <c>null</c> when disabled.
    /// </summary>
    public TimeSpan? IdleTimeout => _cts is null ? null : _idleTimeout;

    /// <summary>
    /// <c>true</c> when <see cref="Token"/> was canceled by the idle timer during an active stream read rather than by the caller.
    /// </summary>
    public bool HasTimedOut =>
        (_timedOut || (_reading && _cts is { IsCancellationRequested: true })) &&
        !_callerToken.IsCancellationRequested;

    /// <summary>
    /// Reads from <paramref name="stream"/> with the idle timer armed only for the duration of the read.
    /// </summary>
    public async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer)
    {
        BeforeRead();
        var succeeded = false;
        try
        {
            var read = await stream.ReadAsync(buffer, Token).ConfigureAwait(false);
            succeeded = true;
            return read;
        }
        finally
        {
            AfterRead(succeeded);
        }
    }

    /// <summary>
    /// Arms the idle timer immediately before awaiting a stream read, refreshing the internal token source if the previous
    /// timer callback fired in the race window after <see cref="AfterRead"/> disarmed it.
    /// </summary>
    public void BeforeRead()
    {
        _timedOut = false;
        if (_cts is null)
        {
            return;
        }

        if (_cts.IsCancellationRequested)
        {
            if (_callerToken.IsCancellationRequested)
            {
                return;
            }

            ReplaceCts();
        }

        _readStartTimestamp = Stopwatch.GetTimestamp();
        _reading = true;
        _cts.CancelAfter(_idleTimeout);

        if (_cts.IsCancellationRequested && !_callerToken.IsCancellationRequested)
        {
            ReplaceCts();
            _readStartTimestamp = Stopwatch.GetTimestamp();
            _cts.CancelAfter(_idleTimeout);
        }
    }

    /// <summary>
    /// Disarms the idle timer immediately after a stream read completes so consumer processing time is never counted as stream idle time.
    /// Preserves <see cref="HasTimedOut"/> when the read was aborted by the idle timer, and resets the token source if the timer
    /// fired or was queued on the thread pool in the race window after <paramref name="succeeded"/> became <c>true</c>.
    /// </summary>
    public void AfterRead(bool succeeded = true)
    {
        _reading = false;
        if (_cts is null)
        {
            return;
        }

        if (!_cts.IsCancellationRequested)
        {
            _cts.CancelAfter(Timeout.InfiniteTimeSpan);
            if (succeeded &&
                !_callerToken.IsCancellationRequested &&
                (_cts.IsCancellationRequested ||
                 (_readStartTimestamp != 0 && Stopwatch.GetElapsedTime(_readStartTimestamp) + s_timerResolutionGuard >= _idleTimeout)))
            {
                // A read that completed right as _idleTimeout elapsed may have a TimerQueueTimer callback already
                // dequeued on the ThreadPool (which CancelAfter(Infinite) cannot abort). Retire the CTS so any
                // in-flight callback cancels the retired instance rather than the next read.
                ReplaceCts();
            }

            return;
        }

        if (_callerToken.IsCancellationRequested)
        {
            return;
        }

        if (succeeded)
        {
            // The timer fired in the tiny window after the read had already returned data.
            // Replace the spent CTS so the next read starts with an uncancelled token instead of throwing a false TimeoutException.
            ReplaceCts();
        }
        else
        {
            _timedOut = true;
        }
    }

    private void ReplaceCts()
    {
        var expired = _cts;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(_callerToken);
        expired?.Dispose();
    }

    public void Dispose() => _cts?.Dispose();
}

