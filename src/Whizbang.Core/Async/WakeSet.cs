// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using System.Threading.Tasks.Sources;

namespace Whizbang.Core.Async;

/// <summary>
/// The wake sources of a long-running loop, waited on together pass after pass with nothing left behind: "wait until any
/// of these fires, or the timeout passes, or the loop is stopped". The one way to race waits in this library; the
/// repository analyzer WHIZ612 reports a hand-written <c>Task.WhenAny</c> over waits.
/// </summary>
/// <remarks>
/// <para>
/// A loop that races fresh waits in <c>Task.WhenAny</c> every pass abandons the losers. A channel keeps an abandoned
/// wait queued until an item arrives or its token is canceled, a semaphore keeps its waiter, and a delay holds its timer
/// and its registration on the token until it fires. On the host's stopping token "canceled" means never, so an idle
/// instance grows by the losers of every pass (#728, #2613). This type is that race written once, so a loop cannot get
/// it wrong by hand:
/// </para>
/// <list type="bullet">
/// <item><description>Each source keeps one pending wait across passes and asks for a new one only after it completed. A
/// pass that another source ends leaves the wait in place for the next pass, never a second one.</description></item>
/// <item><description>The timeout is one timer the set owns, made on the first pass that has a timeout, armed before the
/// sources are and stopped as soon as another source wins. No pass leaves a delay running.</description></item>
/// <item><description>Every wait the set makes runs on <see cref="Token"/>, linked to the loop's. Disposing the set
/// cancels it, which releases every pending wait it made: a channel's queued reader, the wake signal's waiter, a
/// semaphore's queued acquire, the continuation on a gate's task. A set made for one call leaves nothing behind when the
/// call returns.</description></item>
/// <item><description>A permit a semaphore source acquired and the loop did not take is handed back exactly once, when
/// the set is disposed or, if the acquire lands later, as it lands.</description></item>
/// <item><description>The idle path allocates nothing: a pass that parks reuses the pending waits, the timer and the
/// awaitable.</description></item>
/// </list>
/// <para>
/// Sources are levels, as with <see cref="Task.WhenAny(Task[])"/>: a channel with an item, a raised signal, a held
/// permit or a completed gate wakes every pass until the loop drains, consumes, takes or stops it. A
/// <see cref="WakeSignal"/> source is consumed on every wake the set returns other than a stop, before the loop looks
/// for work, so a signal raised after that wakes the following pass. One wait at a time: the set is the loop's, not
/// shared.
/// </para>
/// </remarks>
/// <docs>contributors/loop-waits</docs>
/// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs</tests>
public sealed class WakeSet : IValueTaskSource<Wake>, IDisposable {
  private const int MAX_SOURCES = 64;

  private readonly Lock _gate = new();
  private readonly TimeProvider _time;
  private readonly CancellationTokenSource _stop;
  private readonly CancellationTokenRegistration _stopped;
  private readonly List<WakeSource> _sources = [];
  private readonly Action _onSourceReady;
  private ITimer? _timer;
  private ManualResetValueTaskSourceCore<Wake> _core;
  private bool _parked;
  private bool _timeoutStarted;
  private bool _timedOut;
  private bool _disposed;

  /// <summary>Creates the set for a loop.</summary>
  /// <param name="time">The clock the timeout runs on.</param>
  /// <param name="stoppingToken">Ends the loop: a stop wakes the pending wait, and every later wait, as stopped.</param>
  /// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs:Misuse_IsRefusedAsync</tests>
  public WakeSet(TimeProvider time, CancellationToken stoppingToken) {
    ArgumentNullException.ThrowIfNull(time);
    _time = time;
    _core.RunContinuationsAsynchronously = true;
    _onSourceReady = _tryWake;
    _stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    _stopped = _stop.Token.UnsafeRegister(static state => ((WakeSet)state!)._tryWake(), this);
  }

  /// <summary>
  /// The token every wait the set makes runs on: canceled when the loop's token is, and when the set is disposed. Hand it
  /// to a wait made outside the set (a readiness gate's wait, say) so disposing the set releases that wait too.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs:Dispose_CancelsTheSetsToken_AndSoAWaitMadeOnItAsync</tests>
  public CancellationToken Token => _stop.Token;

  /// <summary>Adds a channel: it wakes the loop when an item can be read, or once the channel is completed.</summary>
  /// <typeparam name="T">The channel's item type.</typeparam>
  /// <param name="reader">The channel's reader. The loop reads it; the set only waits on it.</param>
  /// <returns>The source, to test a wake against and to ask whether the channel completed.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs:WaitAsync_AnItemOnOneChannel_WakesWithThatChannelOnlyAsync</tests>
  public ChannelWakeSource<T> Add<T>(ChannelReader<T> reader) {
    ArgumentNullException.ThrowIfNull(reader);
    return _add(new ChannelWakeSource<T>(reader));
  }

  /// <summary>Adds a wake signal: it wakes the loop when the signal is raised, and is consumed on every wake.</summary>
  /// <param name="signal">The signal.</param>
  /// <returns>The source, to test a wake against.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs:ASignal_WakesTheSet_AndIsConsumedOnEveryWakeAsync</tests>
  public WakeSource Add(WakeSignal signal) {
    ArgumentNullException.ThrowIfNull(signal);
    return _add(new SignalWakeSource(signal));
  }

  /// <summary>
  /// Adds a task that completes once, such as a readiness gate or work the loop waits out: it wakes the loop once it has
  /// completed, and on every pass after. The set waits on it through <see cref="Token"/>, so disposing the set takes the
  /// set's continuation off a task that never completes.
  /// </summary>
  /// <param name="once">The task.</param>
  /// <returns>The source, to test a wake against.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs:AGate_WakesTheSetOnceOpen_AndOnEveryPassAfterAsync</tests>
  public WakeSource Add(Task once) {
    ArgumentNullException.ThrowIfNull(once);
    return _add(new TaskWakeSource(once));
  }

  /// <summary>
  /// Adds a semaphore: it wakes the loop once a permit is acquired for it. The loop owns the permit only after
  /// <see cref="PermitWakeSource.TryTake"/>; a permit it does not take is handed back when the set is disposed, and one
  /// acquired after that is handed back as it lands, so a permit is never kept by a source that lost or double-counted.
  /// </summary>
  /// <param name="semaphore">The semaphore.</param>
  /// <returns>The source, to take the permit from.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Async/WakeSetPermitTests.cs</tests>
  public PermitWakeSource Add(SemaphoreSlim semaphore) {
    ArgumentNullException.ThrowIfNull(semaphore);
    return _add(new PermitWakeSource(semaphore));
  }

  /// <summary>
  /// Starts the next pass's timeout now, before work whose duration should count against it (a read the loop does
  /// before it waits, say). A timeout that passes before <see cref="WaitAsync()"/> makes that wait end at once as a
  /// timeout. Starting it again replaces it.
  /// </summary>
  /// <param name="timeout">How long from now the next pass may wait; <see cref="Timeout.InfiniteTimeSpan"/> for none.</param>
  /// <exception cref="ArgumentOutOfRangeException">The timeout is negative and not infinite.</exception>
  /// <exception cref="InvalidOperationException">A wait is already pending.</exception>
  /// <exception cref="ObjectDisposedException">The set was disposed.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs:ATimeoutStartedBeforeTheWork_CountsTheWorkAgainstThePassAsync</tests>
  public void StartTimeout(TimeSpan timeout) {
    if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) {
      throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A timeout is zero or more, or infinite.");
    }
    lock (_gate) {
      _refuseWhileParkedOrDisposed();
      _timeoutStarted = timeout != Timeout.InfiniteTimeSpan;
      _timedOut = timeout == TimeSpan.Zero;
    }
    // Armed before anything else the pass does: time that passes from here counts against it.
    if (_timeoutStarted && !_timedOut) {
      _armTimer(timeout);
    }
  }

  /// <summary>
  /// Waits until a source fires, the timeout passes or the loop stops, and says which. Never throws for a source that
  /// faulted or was canceled: as with <see cref="Task.WhenAny(Task[])"/>, it is reported as having fired. The same as
  /// <see cref="StartTimeout"/> followed by <see cref="WaitAsync()"/>.
  /// </summary>
  /// <param name="timeout">How long the pass may wait; <see cref="Timeout.InfiniteTimeSpan"/> for no timeout.</param>
  /// <returns>The wake.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The timeout is negative and not infinite.</exception>
  /// <exception cref="InvalidOperationException">A wait is already pending.</exception>
  /// <exception cref="ObjectDisposedException">The set was disposed.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs</tests>
  public ValueTask<Wake> WaitAsync(TimeSpan timeout) {
    StartTimeout(timeout);
    return WaitAsync();
  }

  /// <summary>
  /// Waits until a source fires, the timeout <see cref="StartTimeout"/> started passes, or the loop stops, and says
  /// which. With no timeout started, waits for a source or the stop.
  /// </summary>
  /// <returns>The wake.</returns>
  /// <exception cref="InvalidOperationException">A wait is already pending.</exception>
  /// <exception cref="ObjectDisposedException">The set was disposed.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs</tests>
  public ValueTask<Wake> WaitAsync() {
    lock (_gate) {
      _refuseWhileParkedOrDisposed();
    }
    if (_stop.IsCancellationRequested) {
      _endTimeout();
      return new ValueTask<Wake>(Wake.Stopped);
    }
    _armSources();
    var ready = _readySources();
    var version = (short)0;
    var parks = false;
    lock (_gate) {
      if (ready == 0 && !_timedOut) {
        _core.Reset();
        version = _core.Version;
        _parked = parks = true;
      }
    }
    if (!parks) {
      _endTimeout();
      return new ValueTask<Wake>(_woken(ready != 0 ? WakeReason.Source : WakeReason.Timeout, ready));
    }
    // A source that fired while being armed, or a stop that landed, finds the pass parked.
    _tryWake();
    return new ValueTask<Wake>(this, version);
  }

  /// <summary>
  /// Cancels every wait the set made, which releases them where they are held, hands back every permit not taken, and
  /// wakes a pending wait as stopped.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs:Dispose_ReleasesEveryWaitTheSetMadeAsync</tests>
  public void Dispose() {
    lock (_gate) {
      if (_disposed) {
        return;
      }
      _disposed = true;
    }
    _stop.Cancel();
    _stopped.Dispose();
    _timer?.Dispose();
    foreach (var source in _sources) {
      source.OnDisposed();
    }
    _stop.Dispose();
  }

  /// <inheritdoc />
  Wake IValueTaskSource<Wake>.GetResult(short token) => _core.GetResult(token);

  /// <inheritdoc />
  ValueTaskSourceStatus IValueTaskSource<Wake>.GetStatus(short token) => _core.GetStatus(token);

  /// <inheritdoc />
  void IValueTaskSource<Wake>.OnCompleted(
      Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
    _core.OnCompleted(continuation, state, token, flags);

  private TSource _add<TSource>(TSource source) where TSource : WakeSource {
    lock (_gate) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (_parked) {
        throw new InvalidOperationException("Sources are added between waits, not while one is pending.");
      }
      if (_sources.Count == MAX_SOURCES) {
        throw new InvalidOperationException($"A wake set holds at most {MAX_SOURCES} sources.");
      }
      source.Bit = 1UL << _sources.Count;
      _sources.Add(source);
    }
    return source;
  }

  /// <summary>
  /// Arms the set's one timer for this pass. The first pass that has a timeout makes it, already armed, exactly as a
  /// delay would be made; later passes re-arm it.
  /// </summary>
  private void _armTimer(TimeSpan timeout) {
    if (_timer is null) {
      _timer = _time.CreateTimer(static state => ((WakeSet)state!)._onTimer(), this, timeout, Timeout.InfiniteTimeSpan);
    } else {
      _timer.Change(timeout, Timeout.InfiniteTimeSpan);
    }
  }

  private void _armSources() {
    var token = _stop.Token;
    foreach (var source in _sources) {
      source.Arm(_onSourceReady, token);
    }
  }

  private ulong _readySources() {
    var ready = 0UL;
    foreach (var source in _sources) {
      if (source.IsReady) {
        ready |= source.Bit;
      }
    }
    return ready;
  }

  /// <summary>Wakes the parked wait if a stop landed or a source fired; nothing when no wait is parked.</summary>
  private void _tryWake() {
    var stopped = _stop.IsCancellationRequested;
    var ready = stopped ? 0 : _readySources();
    if (!stopped && ready == 0) {
      return;
    }
    if (_unpark()) {
      _core.SetResult(stopped ? Wake.Stopped : _woken(WakeReason.Source, ready));
    }
  }

  /// <summary>The timer fired: the parked pass, if any, ends as a timeout.</summary>
  /// <remarks>
  /// A fire is taken as this pass's timeout. A fire already under way when another source won an earlier pass can land
  /// on the next one and end it early as a timeout; for a loop that is one extra idle tick, never a lost wake, and
  /// telling it apart would mean reading the clock, which a fake clock that fires without moving time would defeat.
  /// </remarks>
  private void _onTimer() {
    lock (_gate) {
      if (!_timeoutStarted) {
        return;
      }
      if (!_parked) {
        // The timeout passed before the pass parked: the wait that follows ends at once.
        _timedOut = true;
        return;
      }
    }
    if (_unpark()) {
      _core.SetResult(_woken(WakeReason.Timeout, 0));
    }
  }

  /// <summary>Claims the parked wait for one waker; false when another waker, or none, holds it.</summary>
  private bool _unpark() {
    lock (_gate) {
      if (!_parked) {
        return false;
      }
      _parked = false;
    }
    _endTimeout();
    return true;
  }

  /// <summary>The pass is over: its timeout, if one was started, is stopped and forgotten.</summary>
  private void _endTimeout() {
    bool started;
    lock (_gate) {
      started = _timeoutStarted;
      _timeoutStarted = false;
      _timedOut = false;
    }
    if (started) {
      _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
  }

  private void _refuseWhileParkedOrDisposed() {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_parked) {
      throw new InvalidOperationException("A wake set serves one wait at a time; the previous wait is still pending.");
    }
  }

  /// <summary>A wake the loop is about to act on: signals are consumed first, so one raised from here wakes the next pass.</summary>
  private Wake _woken(WakeReason reason, ulong ready) {
    foreach (var source in _sources) {
      source.OnWoken();
    }
    return new Wake(reason, ready);
  }
}

/// <summary>Why a <see cref="WakeSet"/> wait ended.</summary>
/// <docs>contributors/loop-waits</docs>
public enum WakeReason : byte {
  /// <summary>One or more sources fired.</summary>
  Source = 0,

  /// <summary>The timeout passed with no source firing.</summary>
  Timeout = 1,

  /// <summary>The loop's stopping token was canceled, or the set was disposed.</summary>
  Stopped = 2,
}

/// <summary>The end of a <see cref="WakeSet"/> wait: why, and which sources were ready.</summary>
/// <param name="Reason">Why the wait ended.</param>
/// <param name="Ready">The ready sources, one bit each.</param>
/// <docs>contributors/loop-waits</docs>
/// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs</tests>
public readonly record struct Wake(WakeReason Reason, ulong Ready) {
  internal static readonly Wake Stopped = new(WakeReason.Stopped, 0);

  /// <summary>True when the loop was stopped: it leaves.</summary>
  public bool IsStopped => Reason == WakeReason.Stopped;

  /// <summary>True when the timeout passed with no source firing.</summary>
  public bool IsTimeout => Reason == WakeReason.Timeout;

  /// <summary>True when <paramref name="source"/> was ready when the wait ended.</summary>
  /// <param name="source">A source of the set.</param>
  public bool Woke(WakeSource source) {
    ArgumentNullException.ThrowIfNull(source);
    return (Ready & source.Bit) != 0;
  }
}

/// <summary>One wake source of a <see cref="WakeSet"/>; keeps one pending wait across the set's passes.</summary>
/// <docs>contributors/loop-waits</docs>
public abstract class WakeSource {
  private Task? _hooked;

  /// <summary>Only the sources this library defines exist.</summary>
  private protected WakeSource() { }

  internal ulong Bit { get; set; }

  /// <summary>
  /// What a source holds before its first pass, and a permit source after its permit is taken: never completes, so the
  /// source is not ready, and is replaced by a real wait on the next pass.
  /// </summary>
  private protected static Task<bool> Unarmed { get; } = new TaskCompletionSource<bool>().Task;

  /// <summary>The pending wait, or <see cref="Unarmed"/>.</summary>
  private protected abstract Task Pending { get; }

  internal bool IsReady => Pending.IsCompleted;

  /// <summary>Makes sure a wait is pending, and that the set hears once, and only once, when it completes.</summary>
  internal void Arm(Action onReady, CancellationToken token) {
    var pending = Renew(token);
    if (!ReferenceEquals(pending, _hooked) && !pending.IsCompleted) {
      _hooked = pending;
      pending.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(onReady);
    }
  }

  /// <summary>The pending wait, replaced only when there is none or it has completed.</summary>
  private protected abstract Task Renew(CancellationToken token);

  /// <summary>Runs on every wake but a stop.</summary>
  internal virtual void OnWoken() { }

  /// <summary>Runs once the set is disposed and its token canceled.</summary>
  internal virtual void OnDisposed() { }
}

/// <summary>A channel's wait for an item; <see cref="IsChannelCompleted"/> once its writer completed it.</summary>
/// <typeparam name="T">The channel's item type.</typeparam>
/// <docs>contributors/loop-waits</docs>
public sealed class ChannelWakeSource<T> : WakeSource {
  private readonly ChannelReader<T> _reader;
  private Task<bool> _pending = Unarmed;

  internal ChannelWakeSource(ChannelReader<T> reader) => _reader = reader;

  /// <summary>True when the last wait found the channel completed: no item will come.</summary>
  public bool IsChannelCompleted => _pending is { IsCompletedSuccessfully: true, Result: false };

  /// <summary>
  /// The wait this source last made. Awaited after a wake that names the source, it gives the wait's answer (true for an
  /// item, false once the channel completed) or rethrows its fault or cancellation, as awaiting the task a
  /// <c>Task.WhenAny</c> returned did.
  /// </summary>
  public Task<bool> LastWait => _pending;

  private protected override Task Pending => _pending;

  private protected override Task Renew(CancellationToken token) {
    if (_pending.IsCompleted || ReferenceEquals(_pending, Unarmed)) {
      _pending = _reader.WaitToReadAsync(token).AsTask();
    }
    return _pending;
  }
}

/// <summary>A wake signal's wait, consumed on every wake.</summary>
internal sealed class SignalWakeSource(WakeSignal signal) : WakeSource {
  private Task _pending = Unarmed;

  private protected override Task Pending => _pending;

  // WakeSignal hands back its one pending waiter while there is one, or a completed task while it is raised.
  private protected override Task Renew(CancellationToken token) => _pending = signal.WaitAsync(token);

  internal override void OnWoken() => signal.Consume();
}

/// <summary>A task that completes once, waited on through a token the set owns.</summary>
internal sealed class TaskWakeSource(Task once) : WakeSource {
  private Task _pending = Unarmed;

  private protected override Task Pending => _pending;

  private protected override Task Renew(CancellationToken token) {
    if (ReferenceEquals(_pending, Unarmed)) {
      _pending = once.IsCompleted ? once : once.WaitAsync(token);
    }
    return _pending;
  }
}

/// <summary>
/// A semaphore's acquire. Ready once a permit is acquired; the loop owns it after <see cref="TryTake"/>. A permit not
/// taken is handed back exactly once: when the set is disposed, or as a late acquire lands after that.
/// </summary>
/// <docs>contributors/loop-waits</docs>
/// <tests>tests/Whizbang.Core.Tests/Async/WakeSetPermitTests.cs</tests>
public sealed class PermitWakeSource : WakeSource {
  private readonly SemaphoreSlim _semaphore;
  private readonly Lock _permit = new();
  private Task<bool> _pending = Unarmed;
  private bool _disposed;

  internal PermitWakeSource(SemaphoreSlim semaphore) => _semaphore = semaphore;

  private protected override Task Pending => _pending;

  /// <summary>
  /// Takes the permit this source acquired: true once per acquired permit, which the caller then owns and releases;
  /// false when none is held.
  /// </summary>
  public bool TryTake() {
    lock (_permit) {
      // An acquire with no timeout that completed successfully holds a permit; one canceled holds none.
      if (!_pending.IsCompletedSuccessfully) {
        return false;
      }
      _pending = Unarmed;
      return true;
    }
  }

  private protected override Task Renew(CancellationToken token) {
    lock (_permit) {
      // A held permit stays held, and ready, until it is taken; a canceled acquire holds nothing and is not renewed.
      if (ReferenceEquals(_pending, Unarmed)) {
        var acquire = _semaphore.WaitAsync(Timeout.Infinite, token);
        _pending = acquire;
        if (!acquire.IsCompleted) {
          _ = acquire.ContinueWith(
            static (_, state) => ((PermitWakeSource)state!)._handBackIfOrphaned(), this,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
      }
      return _pending;
    }
  }

  internal override void OnDisposed() {
    lock (_permit) {
      _disposed = true;
    }
    _handBackIfOrphaned();
  }

  /// <summary>Hands back a permit acquired and not taken once the set is gone; at most once, since it clears the acquire.</summary>
  private void _handBackIfOrphaned() {
    lock (_permit) {
      if (!_disposed || !_pending.IsCompletedSuccessfully) {
        return;
      }
      _pending = Unarmed;
    }
    _semaphore.Release();
  }
}
