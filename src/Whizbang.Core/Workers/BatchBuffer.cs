// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// The process-wide bound on what the outbox and inbox batch strategies hold in memory (#2220). An append takes room
/// before it reaches the strategy and gives it back when the message leaves the buffer for good: stored, handed to the
/// flush-failure callback, or refused by the strategy. While the buffer is full an append waits for room, signaled by
/// those releases, so a producer slows to the store's pace instead of growing the heap.
/// </summary>
/// <remarks>
/// <para>
/// Room is counted in messages and bytes; each limit is optional and an append waits until every configured limit has
/// room. A message larger than the byte limit is admitted alone into an empty buffer, so it never waits forever.
/// </para>
/// <para>
/// Non-interactive callers see each limit less the interactive reserve (the work coordinator gate's reserve fraction),
/// so bulk work cannot starve user-facing writes. Waiters of one kind are served in arrival order, and a later standard
/// caller never passes an earlier one that is still waiting.
/// </para>
/// <para>
/// A caller whose scope has a database transaction open may go past the limits, up to
/// <see cref="BatchBufferLimits.TransactionCeilingMultiplier"/> times them, instead of waiting: it holds a connection the
/// flushes may need. Each such overshoot is counted and logged with the caller's name. Past that ceiling it waits too.
/// </para>
/// </remarks>
/// <docs>messaging/outbox-pattern#bounded-buffer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/BatchBufferTests.cs</tests>
internal sealed partial class BatchBuffer {
  private readonly BatchBufferLimits _limits;
  private readonly ILogger _logger;
  private readonly WorkCoordinatorMetrics? _metrics;
  private readonly TimeProvider _time;
  private readonly Lock _lock = new();
  private readonly LinkedList<Waiter> _waiters = [];
  private readonly Dictionary<object, Held> _held = new(ReferenceEqualityComparer.Instance);
  private long _messages;
  private long _bytes;
  private int _standardWaiters;
  private int _interactiveWaiters;
  private long _waitsThisEpisode;
  private bool _stopped;

  /// <summary>Creates the buffer.</summary>
  /// <param name="limits">The limits, the reserve and the transaction ceiling.</param>
  /// <param name="logger">Where the wait and overshoot warnings go.</param>
  /// <param name="metrics">The work coordinator metrics, or null when metrics are off.</param>
  /// <param name="timeProvider">Measures how long appends wait.</param>
  public BatchBuffer(BatchBufferLimits limits, ILogger<BatchBuffer> logger, WorkCoordinatorMetrics? metrics, TimeProvider timeProvider) {
    ArgumentNullException.ThrowIfNull(limits);
    ArgumentNullException.ThrowIfNull(logger);
    ArgumentNullException.ThrowIfNull(timeProvider);
    _limits = limits;
    _logger = logger;
    _metrics = metrics;
    _time = timeProvider;
    metrics?.ObserveBatchBuffer(() => Volatile.Read(ref _messages), () => Volatile.Read(ref _bytes));
  }

  /// <summary>The limits the buffer enforces.</summary>
  public BatchBufferLimits Limits => _limits;

  /// <summary>Messages buffered now.</summary>
  public long BufferedMessages {
    get {
      lock (_lock) {
        return _messages;
      }
    }
  }

  /// <summary>Bytes buffered now.</summary>
  public long BufferedBytes {
    get {
      lock (_lock) {
        return _bytes;
      }
    }
  }

  /// <summary>Appends waiting for room now.</summary>
  public int WaitingAppends {
    get {
      lock (_lock) {
        return _waiters.Count;
      }
    }
  }

  /// <summary>
  /// Takes room for <paramref name="message"/>, waiting until there is some. Returns when the message may be appended.
  /// </summary>
  /// <param name="message">The message, held by reference until it is released.</param>
  /// <param name="bytes">Its size.</param>
  /// <param name="interactive">Whether the caller is interactive and may use the reserve.</param>
  /// <param name="context">Who is appending, and whether their scope holds a transaction open.</param>
  /// <param name="cancellationToken">Ends the wait.</param>
  /// <exception cref="OperationCanceledException">The token was canceled, or the buffer stopped, while waiting.</exception>
  public ValueTask AdmitAsync(object message, long bytes, bool interactive, BatchAppendContext context, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(message);
    Waiter waiter;
    lock (_lock) {
      if (!_queuedAhead(interactive) && _fits(bytes, interactive, 1)) {
        _take(message, bytes);
        return ValueTask.CompletedTask;
      }
      // Asked only when the append would otherwise wait, so the common path never pays for it.
      if (context.HoldsOpenTransaction?.Invoke() == true && _fits(bytes, interactive, _limits.TransactionCeilingMultiplier)) {
        _take(message, bytes);
        _metrics?.BatchBufferCeilingOvershoots.Add(1);
        LogCeilingOvershoot(_logger, context.Caller, _messages, _bytes);
        return ValueTask.CompletedTask;
      }
      if (_stopped) {
        throw new OperationCanceledException("The batch buffer is stopping; the append was not admitted.");
      }
      cancellationToken.ThrowIfCancellationRequested();
      waiter = new Waiter(message, bytes, interactive, _time.GetTimestamp());
      waiter.Node = _waiters.AddLast(waiter);
      if (interactive) {
        _interactiveWaiters++;
      } else {
        _standardWaiters++;
      }
      if (_waitsThisEpisode++ == 0) {
        LogWaitsStarted(_logger, _messages, _bytes, _limits.MaxMessages, _limits.MaxBytes);
      }
      _metrics?.BatchBufferAppendWaits.Add(1);
    }
    if (cancellationToken.CanBeCanceled) {
      waiter.Attach(cancellationToken.Register(static state => {
        var (buffer, w, token) = ((BatchBuffer, Waiter, CancellationToken))state!;
        buffer._cancel(w, token);
      }, (this, waiter, cancellationToken)));
    }
    return new ValueTask(waiter.Completion.Task);
  }

  /// <summary>Gives back the room <paramref name="messages"/> took. A message that holds no room is skipped.</summary>
  /// <param name="messages">Messages that left the buffer for good.</param>
  public void Release(IEnumerable<object> messages) {
    ArgumentNullException.ThrowIfNull(messages);
    List<Waiter>? admitted;
    lock (_lock) {
      foreach (var message in messages) {
        _give(message);
      }
      admitted = _admitWaiters();
    }
    _complete(admitted);
  }

  /// <summary>Gives back the room one message took. A message that holds no room is skipped.</summary>
  /// <param name="message">The message.</param>
  public void ReleaseOne(object message) => Release([message]);

  /// <summary>The outbox store callback, giving a batch's room back once the batch is stored.</summary>
  /// <param name="store">The callback that stores a batch.</param>
  /// <returns>The wrapped callback. A failed store gives nothing back: the batch is retried, or reported as failed.</returns>
  public OutboxBulkFlushCallback ReleasingOnStore(OutboxBulkFlushCallback store) {
    ArgumentNullException.ThrowIfNull(store);
    return async (messages, cancellationToken) => {
      await store(messages, cancellationToken).ConfigureAwait(false);
      Release(messages);
    };
  }

  /// <summary>The inbox store callback, giving a batch's room back once the batch is stored.</summary>
  /// <param name="store">The callback that stores a batch.</param>
  /// <returns>The wrapped callback. A failed store gives nothing back: the batch is retried, or reported as failed.</returns>
  public InboxBulkFlushCallback ReleasingOnStore(InboxBulkFlushCallback store) {
    ArgumentNullException.ThrowIfNull(store);
    return async (messages, cancellationToken) => {
      await store(messages, cancellationToken).ConfigureAwait(false);
      Release(messages);
    };
  }

  /// <summary>
  /// The outbox flush-failure callback, giving a batch's room back once it is reported: the batch has left the buffer
  /// for good, dead-lettered or not.
  /// </summary>
  /// <param name="record">The callback that records a failed batch.</param>
  /// <returns>The wrapped callback.</returns>
  public OutboxFlushFailureCallback ReleasingOnFailure(OutboxFlushFailureCallback record) {
    ArgumentNullException.ThrowIfNull(record);
    return async (failure, cancellationToken) => {
      try {
        return await record(failure, cancellationToken).ConfigureAwait(false);
      } finally {
        Release(failure.Messages);
      }
    };
  }

  /// <summary>
  /// The inbox flush-failure callback, giving a batch's room back once it is reported: the batch has left the buffer
  /// for good, dead-lettered or not.
  /// </summary>
  /// <param name="record">The callback that records a failed batch.</param>
  /// <returns>The wrapped callback.</returns>
  public InboxFlushFailureCallback ReleasingOnFailure(InboxFlushFailureCallback record) {
    ArgumentNullException.ThrowIfNull(record);
    return async (failure, cancellationToken) => {
      try {
        return await record(failure, cancellationToken).ConfigureAwait(false);
      } finally {
        Release(failure.Messages);
      }
    };
  }

  /// <summary>
  /// Stops the buffer at shutdown: every waiting append is canceled, and an append that would have to wait is refused.
  /// </summary>
  public void Stop() {
    List<Waiter> canceled;
    lock (_lock) {
      _stopped = true;
      canceled = [.. _waiters];
      _waiters.Clear();
      _standardWaiters = 0;
      _interactiveWaiters = 0;
      _endEpisode();
    }
    // Each registration stays until its token goes; a cancellation after this finds its waiter already gone.
    foreach (var waiter in canceled) {
      waiter.Completion.TrySetException(new OperationCanceledException("The batch buffer stopped while the append waited for room."));
    }
  }

  private bool _queuedAhead(bool interactive) => interactive ? _interactiveWaiters > 0 : _standardWaiters > 0;

  /// <summary>Whether a message of <paramref name="bytes"/> fits within <paramref name="ceiling"/> times the caller's limits.</summary>
  private bool _fits(long bytes, bool interactive, double ceiling) {
    if (_messages == 0) {
      // An empty buffer admits anything, alone if it is larger than the byte limit.
      return true;
    }
    var share = (interactive ? 1 : 1 - _limits.InteractiveReserveFraction) * ceiling;
    return _within(_messages + 1, _limits.MaxMessages, share) && _within(_bytes + bytes, _limits.MaxBytes, share);
  }

  private static bool _within(long total, long? limit, double share) => limit is not { } max || total <= (long)(max * share);

  private void _take(object message, long bytes) {
    _messages++;
    _bytes += bytes;
    if (_held.TryGetValue(message, out var held)) {
      held.Count++;
    } else {
      _held[message] = new Held(bytes);
    }
  }

  private void _give(object message) {
    if (!_held.TryGetValue(message, out var held)) {
      return;
    }
    _messages--;
    _bytes -= held.Bytes;
    if (--held.Count == 0) {
      _held.Remove(message);
    }
  }

  /// <summary>Admits every waiter that now fits, in arrival order per kind. Called under the lock.</summary>
  private List<Waiter>? _admitWaiters() {
    List<Waiter>? admitted = null;
    var standardBlocked = false;
    var interactiveBlocked = false;
    var node = _waiters.First;
    while (node is not null) {
      var next = node.Next;
      var waiter = node.Value;
      var blocked = waiter.Interactive ? interactiveBlocked : standardBlocked;
      if (!blocked && _fits(waiter.Bytes, waiter.Interactive, 1)) {
        _take(waiter.Message, waiter.Bytes);
        _unlink(waiter);
        (admitted ??= []).Add(waiter);
      } else if (waiter.Interactive) {
        interactiveBlocked = true;
      } else {
        standardBlocked = true;
      }
      node = next;
    }
    if (_waiters.Count == 0) {
      _endEpisode();
    }
    return admitted;
  }

  private void _complete(List<Waiter>? admitted) {
    if (admitted is null) {
      return;
    }
    foreach (var waiter in admitted) {
      waiter.Detach();
      _metrics?.BatchBufferWaitDuration.Record(_time.GetElapsedTime(waiter.Started).TotalMilliseconds);
      waiter.Completion.TrySetResult();
    }
  }

  private void _cancel(Waiter waiter, CancellationToken token) {
    List<Waiter>? admitted;
    lock (_lock) {
      if (waiter.Node!.List is null) {
        return;
      }
      _unlink(waiter);
      // A canceled waiter at the head may have been what held the others back.
      admitted = _admitWaiters();
    }
    waiter.Completion.TrySetCanceled(token);
    _complete(admitted);
  }

  private void _unlink(Waiter waiter) {
    _waiters.Remove(waiter.Node!);
    if (waiter.Interactive) {
      _interactiveWaiters--;
    } else {
      _standardWaiters--;
    }
  }

  private void _endEpisode() {
    if (_waitsThisEpisode > 0) {
      LogWaitsStopped(_logger, _waitsThisEpisode, _messages, _bytes);
      _waitsThisEpisode = 0;
    }
  }

  private sealed class Held(long bytes) {
    public long Bytes { get; } = bytes;
    public int Count { get; set; } = 1;
  }

  /// <summary>One append waiting for room.</summary>
  internal sealed class Waiter(object message, long bytes, bool interactive, long started) {
    public object Message { get; } = message;
    public long Bytes { get; } = bytes;
    public bool Interactive { get; } = interactive;
    public long Started { get; } = started;
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public LinkedListNode<Waiter>? Node { get; set; }

    private readonly Lock _registrationLock = new();
    private CancellationTokenRegistration _registration;
    private bool _detached;

    /// <summary>Keeps the cancellation registration, or drops it at once when the wait already ended.</summary>
    public void Attach(CancellationTokenRegistration registration) {
      lock (_registrationLock) {
        if (!_detached) {
          _registration = registration;
          return;
        }
      }
      registration.Dispose();
    }

    /// <summary>Ends the wait's cancellation registration; one attached later is dropped as it arrives.</summary>
    public void Detach() {
      CancellationTokenRegistration registration;
      lock (_registrationLock) {
        _detached = true;
        registration = _registration;
      }
      registration.Dispose();
    }
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "The outbox and inbox batch buffer is full ({Messages} messages, {Bytes} bytes; limits {MaxMessages} messages, {MaxBytes} bytes): appends wait for flushes to make room")]
  private static partial void LogWaitsStarted(ILogger logger, long messages, long bytes, long? maxMessages, long? maxBytes);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "The outbox and inbox batch buffer has room again after {Waits} append(s) waited ({Messages} messages, {Bytes} bytes buffered)")]
  private static partial void LogWaitsStopped(ILogger logger, long waits, long messages, long bytes);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "{Caller} holds a database transaction open, so its append went past the batch buffer limit instead of waiting ({Messages} messages, {Bytes} bytes buffered)")]
  private static partial void LogCeilingOvershoot(ILogger logger, string caller, long messages, long bytes);
}
