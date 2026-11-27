// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using System.Threading.Tasks.Sources;

namespace Whizbang.Core.Async;

/// <summary>
/// The wake sources of a long-running loop, waited on together pass after pass with nothing left behind: "wait until any
/// of these fires, or the timeout passes, or the loop is stopped".
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
/// <item><description>The timeout is one timer the set owns, re-armed each pass and stopped as soon as another source
/// wins. No pass leaves a delay running.</description></item>
/// <item><description>Every wait the set makes runs on a token of its own, linked to the loop's. Disposing the set
/// cancels it, which releases every pending wait it made: a channel's queued reader, the wake signal's waiter, the
/// continuation on a gate's task. A set made for one call leaves nothing behind when the call returns.</description></item>
/// <item><description>The idle path allocates nothing: a pass that parks reuses the pending waits, the timer and the
/// awaitable.</description></item>
/// </list>
/// <para>
/// Sources are levels, as with <see cref="Task.WhenAny(Task[])"/>: a channel with an item, a raised signal or a
/// completed gate wakes every pass until the loop drains, consumes or stops it. A <see cref="WakeSignal"/> source is
/// consumed on every wake the set returns other than a stop, before the loop looks for work, so a signal raised after
/// that wakes the following pass. One wait at a time: the set is the loop's, not shared.
/// </para>
/// </remarks>
/// <docs>contributors/loop-waits</docs>
/// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs</tests>
internal sealed class WakeSet : IValueTaskSource<Wake>, IDisposable {
  private const int MAX_SOURCES = 64;

  private readonly Lock _gate = new();
  private readonly CancellationTokenSource _stop;
  private readonly CancellationTokenRegistration _stopped;
  private readonly ITimer _timer;
  private readonly List<WakeSource> _sources = [];
  private readonly Action _onSourceReady;
  private ManualResetValueTaskSourceCore<Wake> _core;
  private bool _parked;
  private TimeSpan _timeout;
  private bool _disposed;

  /// <summary>Creates the set for a loop.</summary>
  /// <param name="time">The clock the timeout runs on.</param>
  /// <param name="stoppingToken">Ends the loop: a stop wakes the pending wait, and every later wait, as canceled.</param>
  public WakeSet(TimeProvider time, CancellationToken stoppingToken) {
    ArgumentNullException.ThrowIfNull(time);
    _core.RunContinuationsAsynchronously = true;
    _onSourceReady = _tryWake;
    _stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    _timer = time.CreateTimer(static state => ((WakeSet)state!)._onTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    _stopped = _stop.Token.UnsafeRegister(static state => ((WakeSet)state!)._tryWake(), this);
  }

  /// <summary>Adds a channel: it wakes the loop when an item can be read, or once the channel is completed.</summary>
  /// <typeparam name="T">The channel's item type.</typeparam>
  /// <param name="reader">The channel's reader. The loop reads it; the set only waits on it.</param>
  /// <returns>The source, to test a wake against and to ask whether the channel completed.</returns>
  public ChannelWakeSource<T> Add<T>(ChannelReader<T> reader) {
    ArgumentNullException.ThrowIfNull(reader);
    var source = new ChannelWakeSource<T>(reader);
    _add(source);
    return source;
  }

  /// <summary>Adds a wake signal: it wakes the loop when the signal is raised, and is consumed on every wake.</summary>
  /// <param name="signal">The signal.</param>
  /// <returns>The source, to test a wake against.</returns>
  public WakeSource Add(WakeSignal signal) {
    ArgumentNullException.ThrowIfNull(signal);
    return _add(new SignalWakeSource(signal));
  }

  /// <summary>
  /// Adds a task that completes once, such as a readiness gate: it wakes the loop once it has completed, and on every
  /// pass after. Disposing the set releases the set's wait on it, so a gate that never opens keeps nothing of the set.
  /// </summary>
  /// <param name="once">The task.</param>
  /// <returns>The source, to test a wake against.</returns>
  public WakeSource Add(Task once) {
    ArgumentNullException.ThrowIfNull(once);
    return _add(new TaskWakeSource(once));
  }

  /// <summary>
  /// Waits until a source fires, the timeout passes or the loop stops, and says which. Never throws for a source that
  /// faulted or was canceled: as with <see cref="Task.WhenAny(Task[])"/>, it is reported as having fired.
  /// </summary>
  /// <param name="timeout">How long the pass may wait; <see cref="Timeout.InfiniteTimeSpan"/> for no timeout.</param>
  /// <returns>The wake.</returns>
  /// <exception cref="InvalidOperationException">A wait is already pending.</exception>
  /// <exception cref="ObjectDisposedException">The set was disposed.</exception>
  public ValueTask<Wake> WaitAsync(TimeSpan timeout) {
    if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) {
      throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A timeout is zero or more, or infinite.");
    }
    lock (_gate) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (_parked) {
        throw new InvalidOperationException("A wake set serves one wait at a time; the previous wait is still pending.");
      }
    }
    if (_stop.IsCancellationRequested) {
      return new ValueTask<Wake>(Wake.Stopped);
    }
    var token = _stop.Token;
    foreach (var source in _sources) {
      source.Arm(_onSourceReady, token);
    }
    var ready = _readySources();
    if (ready != 0) {
      return new ValueTask<Wake>(_woken(WakeReason.Source, ready));
    }
    if (timeout == TimeSpan.Zero) {
      return new ValueTask<Wake>(_woken(WakeReason.Timeout, 0));
    }
    short version;
    lock (_gate) {
      _core.Reset();
      version = _core.Version;
      _parked = true;
      _timeout = timeout;
    }
    if (timeout != Timeout.InfiniteTimeSpan) {
      _timer.Change(timeout, Timeout.InfiniteTimeSpan);
    }
    // A source that fired, or a stop that landed, between the check above and parking found nothing parked to wake.
    _tryWake();
    return new ValueTask<Wake>(this, version);
  }

  /// <summary>
  /// Cancels every wait the set made, which releases them where they are held, and wakes a pending wait as stopped.
  /// </summary>
  public void Dispose() {
    lock (_gate) {
      if (_disposed) {
        return;
      }
      _disposed = true;
    }
    _stop.Cancel();
    _stopped.Dispose();
    _timer.Dispose();
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

  private WakeSource _add(WakeSource source) {
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
      if (!_parked || _timeout == Timeout.InfiniteTimeSpan) {
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
    _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    return true;
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
internal enum WakeReason : byte {
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
internal readonly record struct Wake(WakeReason Reason, ulong Ready) {
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
internal abstract class WakeSource {
  private Task? _hooked;

  internal ulong Bit { get; set; }

  /// <summary>The pending wait, or null before the first pass.</summary>
  protected abstract Task? Pending { get; }

  /// <summary>Whether the pending wait has completed. Read only after <see cref="Arm"/>, so a wait is always pending.</summary>
  internal bool IsReady => Pending!.IsCompleted;

  /// <summary>Makes sure a wait is pending, and that the set hears once, and only once, when it completes.</summary>
  internal void Arm(Action onReady, CancellationToken token) {
    var pending = Renew(token);
    if (!ReferenceEquals(pending, _hooked) && !pending.IsCompleted) {
      _hooked = pending;
      pending.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(onReady);
    }
  }

  /// <summary>The pending wait, replaced only when there is none or it has completed.</summary>
  protected abstract Task Renew(CancellationToken token);

  /// <summary>Runs on every wake but a stop.</summary>
  internal virtual void OnWoken() { }
}

/// <summary>A channel's wait for an item; <see cref="IsChannelCompleted"/> once its writer completed it.</summary>
internal sealed class ChannelWakeSource<T>(ChannelReader<T> reader) : WakeSource {
  private Task<bool>? _pending;

  /// <summary>True when the last wait found the channel completed: no item will come.</summary>
  public bool IsChannelCompleted => _pending is { IsCompletedSuccessfully: true, Result: false };

  protected override Task? Pending => _pending;

  protected override Task Renew(CancellationToken token) {
    if (_pending is not { IsCompleted: false }) {
      _pending = reader.WaitToReadAsync(token).AsTask();
    }
    return _pending;
  }
}

/// <summary>A wake signal's wait, consumed on every wake.</summary>
internal sealed class SignalWakeSource(WakeSignal signal) : WakeSource {
  private Task? _pending;

  protected override Task? Pending => _pending;

  // WakeSignal hands back its one pending waiter while there is one, or a completed task while it is raised.
  protected override Task Renew(CancellationToken token) => _pending = signal.WaitAsync(token);

  internal override void OnWoken() => signal.Consume();
}

/// <summary>A task that completes once, waited on through a token the set owns.</summary>
internal sealed class TaskWakeSource(Task once) : WakeSource {
  private Task? _pending;

  protected override Task? Pending => _pending;

  protected override Task Renew(CancellationToken token) => _pending ??= once.IsCompleted ? once : once.WaitAsync(token);
}
