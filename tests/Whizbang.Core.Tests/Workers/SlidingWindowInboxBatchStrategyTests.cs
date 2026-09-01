using System.Text.Json;
using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the contract of <see cref="SlidingWindowInboxBatchStrategy"/> — the default
/// IInboxBatchStrategy that batches inbox writes via SlidingWindowBatcher. Defaults baked at
/// 50ms / 1s / 100 per plans/pump-then-process.md.
/// </summary>
public class SlidingWindowInboxBatchStrategyTests {
  private readonly Uuid7IdProvider _idProvider = new();

  [Test]
  public async Task AppendAsync_SingleMessage_FlushedAfterSlidingWindowAsync() {
    // Real time provider — FakeTimeProvider is flaky with SlidingWindowBatcher per
    // SlidingWindowBatcherTests' guidance. 30ms sliding / 200ms max-wait is fast + reliable.
    var captured = new List<InboxMessage[]>();
    var flushedSignal = new TaskCompletionSource();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    await sut.AppendAsync(_makeMessage());

    // Sliding-window debounce fires after 30ms idle → flush
    await flushedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(captured.Count).IsEqualTo(1);
    await Assert.That(captured[0].Length).IsEqualTo(1);
  }

  [Test]
  public async Task AppendAsync_BatchExceedsMaxSize_FlushesEarlyAsync() {
    var captured = new List<InboxMessage[]>();
    var firstFlush = new TaskCompletionSource();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        firstFlush.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromSeconds(10),  // generous so MaxSize is what flushes
        MaxSize = 5,
      });

    for (var i = 0; i < 5; i++) {
      await sut.AppendAsync(_makeMessage());
    }

    // MaxSize=5 should trigger flush regardless of timing
    await firstFlush.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(captured.Count).IsEqualTo(1);
    await Assert.That(captured[0].Length).IsEqualTo(5);
  }

  [Test]
  public async Task FlushAndStopAsync_DrainsBufferedMessagesAsync() {
    var captured = new List<InboxMessage[]>();

    var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromMinutes(1),  // very long — won't fire before stop
        MaxSize = 1000,
      });

    await sut.AppendAsync(_makeMessage());
    await sut.AppendAsync(_makeMessage());

    // Stop drains the remaining buffered messages
    await sut.FlushAndStopAsync();

    var totalFlushed = captured.Sum(b => b.Length);
    await Assert.That(totalFlushed).IsEqualTo(2);
  }

  [Test]
  public async Task AppendAsync_AfterStop_ThrowsAsync() {
    var sut = new SlidingWindowInboxBatchStrategy(
      flush: (_, _) => Task.CompletedTask);

    await sut.FlushAndStopAsync();

    // Slice 23: per-stream architecture throws ObjectDisposedException at the AppendAsync
    // entry (mirror of SlidingWindowOutboxBatchStrategy). Prior single-channel impl threw
    // ChannelClosedException from the underlying writer.
    await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
      await sut.AppendAsync(_makeMessage()));
  }

  [Test]
  public async Task DefaultOptions_300ms_3s_1000Async() {
    // Slice 23: per-stream defaults align with the apply boundary's sliding window so
    // fan-in events across transport messages coalesce before flush. Idle eviction
    // bounds memory under many short-lived streams (mirror of outbox slice 9).
    var defaults = new SlidingWindowInboxOptions();
    await Assert.That(defaults.SlidingWindow).IsEqualTo(TimeSpan.FromMilliseconds(300));
    await Assert.That(defaults.MaxWait).IsEqualTo(TimeSpan.FromSeconds(3));
    await Assert.That(defaults.MaxSize).IsEqualTo(1000);
    await Assert.That(defaults.IdleEvictionWindow).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(defaults.IdleSweepInterval).IsEqualTo(TimeSpan.FromSeconds(10));
  }

  [Test]
  public async Task AppendAsync_OutOfOrderArrivals_FlushedSortedByMessageIdAsync() {
    // Slice 18 invariant: every batch boundary delivers event_id-sorted output. Concurrent
    // producers (transport consumers across multiple RabbitMQ channels) can deposit messages
    // into the inbox sliding window in non-deterministic order. Cursor-by-event_id on the
    // perspective-apply side depends on lex order matching commit order, so the window must
    // sort before flushing to wh_inbox.
    var captured = new List<InboxMessage[]>();
    var flushedSignal = new TaskCompletionSource();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    // Generate three messages — Uuid7 provider guarantees m1 < m2 < m3 lex order.
    var m1 = _makeMessage();
    var m2 = _makeMessage();
    var m3 = _makeMessage();

    // Enqueue out-of-order on purpose — mirror of the producer race that triggers
    // cursor inversion downstream.
    await sut.AppendAsync(m3);
    await sut.AppendAsync(m1);
    await sut.AppendAsync(m2);

    await flushedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(captured.Count).IsEqualTo(1);
    var batch = captured[0];
    await Assert.That(batch.Length).IsEqualTo(3);
    // Batch MUST be sorted by MessageId ASC — locks the slice-18 invariant.
    await Assert.That(batch[0].MessageId).IsEqualTo(m1.MessageId);
    await Assert.That(batch[1].MessageId).IsEqualTo(m2.MessageId);
    await Assert.That(batch[2].MessageId).IsEqualTo(m3.MessageId);
  }

  // ===== slice 23: per-stream invariants =====

  [Test]
  public async Task AppendAsync_DifferentStreams_FlushIndependentBatchesAsync() {
    // Slice 23 invariant: each stream gets its own per-stream buffer + drain task.
    // Messages for stream A and stream B MUST NOT be mixed into one flush — that would
    // re-introduce the cross-batch ordering race the slice fixed.
    var captured = new System.Collections.Concurrent.ConcurrentBag<InboxMessage[]>();
    var streamA = _idProvider.NewGuid();
    var streamB = _idProvider.NewGuid();
    var flushedCount = 0;
    var done = new TaskCompletionSource();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, _) => {
        captured.Add(msgs);
        if (Interlocked.Increment(ref flushedCount) == 2) {
          done.TrySetResult();
        }
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(300),
        MaxSize = 100,
      });

    await sut.AppendAsync(_makeMessage(streamA));
    await sut.AppendAsync(_makeMessage(streamA));
    await sut.AppendAsync(_makeMessage(streamB));

    await done.Task.WaitAsync(TimeSpan.FromSeconds(2));

    var batches = captured.ToArray();
    await Assert.That(batches.Length).IsEqualTo(2);
    // Each batch is single-stream. Find each by examining the StreamId of its first message.
    var batchA = System.Linq.Enumerable.Single(batches, b => b[0].StreamId == streamA);
    var batchB = System.Linq.Enumerable.Single(batches, b => b[0].StreamId == streamB);
    await Assert.That(batchA.Length).IsEqualTo(2);
    await Assert.That(batchB.Length).IsEqualTo(1);
  }

  [Test]
  public async Task AppendAsync_SameStream_CrossWindowArrivals_CoalesceInOneFlushAsync() {
    // The motivating fan-in case: stream X receives messages from multiple producers
    // across the sliding window. Per-slice-23 they MUST flush in a single batch (sorted
    // by MessageId), so the downstream apply sees them in order with no inversion.
    var captured = new List<InboxMessage[]>();
    var flushedSignal = new TaskCompletionSource();
    var streamX = _idProvider.NewGuid();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, _) => {
        captured.Add(msgs);
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        // CI under load: Task.Delay(20ms) often actually waits longer than 20ms because the
        // scheduler is busy. Use a generous sliding window (500ms) so all 3 appends land
        // before the window expires, even on slow hosts. MaxWait bumped proportionally.
        SlidingWindow = TimeSpan.FromMilliseconds(500),
        MaxWait = TimeSpan.FromSeconds(3),
        MaxSize = 100,
      });

    // m1, m2, m3 are MessageId-sorted. Append m3 first, m1 second (within window),
    // m2 third (also within window) — mirrors the cross-producer race that produced
    // cursor inversions before slice 23.
    var m1 = _makeMessage(streamX);
    var m2 = _makeMessage(streamX);
    var m3 = _makeMessage(streamX);
    await sut.AppendAsync(m3);
    await Task.Delay(20);
    await sut.AppendAsync(m1);
    await Task.Delay(20);
    await sut.AppendAsync(m2);

    await flushedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(captured.Count).IsEqualTo(1);
    var batch = captured[0];
    await Assert.That(batch.Length).IsEqualTo(3);
    // Slice 18b sort survives — batch is MessageId-ASC even though arrival was out of order.
    await Assert.That(batch[0].MessageId).IsEqualTo(m1.MessageId);
    await Assert.That(batch[1].MessageId).IsEqualTo(m2.MessageId);
    await Assert.That(batch[2].MessageId).IsEqualTo(m3.MessageId);
  }

  [Test]
  public async Task AppendAsync_NullStreamId_RoutesToDefaultBufferAsync() {
    // Broadcast-style messages with no aggregate identity (StreamId == null) MUST route
    // through a single default buffer keyed by Guid.Empty — not create a buffer per null
    // message. Mirrors the SlidingWindowOutboxBatchStrategy._defaultStreamKey behavior.
    var captured = new List<InboxMessage[]>();
    var flushedSignal = new TaskCompletionSource();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, _) => {
        captured.Add(msgs);
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    await sut.AppendAsync(_makeMessage(streamId: null));
    await sut.AppendAsync(_makeMessage(streamId: null));

    await flushedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(captured.Count).IsEqualTo(1);
    await Assert.That(captured[0].Length).IsEqualTo(2);
    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1);
  }

  /// <summary>
  /// Covers the idle-sweep timer callback (static lambda → _fireAndForgetIdleSweep →
  /// _runIdleSweepAsync). A tight IdleSweepInterval ensures the timer fires at least once
  /// during the wait, and a tight IdleEvictionWindow ensures the inactive buffer is
  /// evicted.
  /// </summary>
  [Test]
  public async Task IdleSweep_EvictsInactiveStreamBuffersAsync() {
    var flushedSignal = new TaskCompletionSource();
    var streamId = _idProvider.NewGuid();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (_, _) => {
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(10),
        MaxWait = TimeSpan.FromMilliseconds(50),
        MaxSize = 100,
        IdleSweepInterval = TimeSpan.FromMilliseconds(20),
        IdleEvictionWindow = TimeSpan.FromMilliseconds(20),
      });

    await sut.AppendAsync(_makeMessage(streamId));
    await flushedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
    while (sut.ActiveStreamCount > 0 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(20);
    }
    await Assert.That(sut.ActiveStreamCount).IsEqualTo(0);
  }

  // ===== helpers =====

  private InboxMessage _makeMessage(Guid? streamId = null) {
    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{}").RootElement,
      []);
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "test",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
    };
  }

  // ============================================================
  // Flush failure and shutdown
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task AppendAsync_WhenAFlushFails_TheStreamKeepsAcceptingWorkAsync(
      CancellationToken testToken) {
    // The flush writes to the database, which can be unavailable. Letting that kill the stream's
    // drain loop would silently stop batching for that stream for the life of the process, with
    // messages accepted into a buffer nothing reads.
    var attempts = 0;
    var secondFlush = new TaskCompletionSource();
    var logger = new RecordingLogger();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => {
        var n = Interlocked.Increment(ref attempts);
        if (n == 1) {
          return Task.FromException(new InvalidOperationException("database unavailable"));
        }
        secondFlush.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      },
      logger: logger);

    var streamId = Guid.CreateVersion7();
    await sut.AppendAsync(_makeMessage(streamId), testToken);
    // Give the first (failing) flush time to land before the second batch.
    await Task.Delay(120, testToken);
    await sut.AppendAsync(_makeMessage(streamId), testToken);

    await secondFlush.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    await Assert.That(attempts).IsGreaterThanOrEqualTo(2)
      .Because("a failed flush must not end the stream's drain loop — the next batch still runs");
  }

  [Test]
  [Timeout(30000)]
  public async Task AppendAsync_AFailedFlushIsReportedAsync(CancellationToken testToken) {
    // The batch is dropped on failure and recovered only by transport redelivery, so the log
    // line is the sole record that it happened. Without it a silent drop looks like a message
    // that was never sent.
    var failed = new TaskCompletionSource();
    var logger = new RecordingLogger();

    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => {
        failed.TrySetResult();
        return Task.FromException(new InvalidOperationException("database unavailable"));
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      },
      logger: logger);

    await sut.AppendAsync(_makeMessage(), testToken);
    await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    // The log happens just after the flush task faults.
    await Task.Delay(150, testToken);

    await Assert.That(logger.Errors.Any(e => e.Contains("bulk flush", StringComparison.Ordinal))).IsTrue()
      .Because("the batch is dropped and only transport redelivery recovers it — the log line is "
             + "the only record that it happened");
  }

  [Test]
  [Timeout(30000)]
  public async Task DisposeAsync_IsIdempotentAsync(CancellationToken testToken) {
    // `await using` plus an explicit stop in the host's shutdown is an ordinary shape, and the
    // second pass must not re-dispose the timer or cancel an already-disposed source.
    var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => Task.CompletedTask,
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    await sut.FlushAndStopAsync(testToken);
    await sut.DisposeAsync();
    await sut.DisposeAsync();
  }

  [Test]
  [Timeout(30000)]
  public async Task FlushAndStop_WithNothingBuffered_IsCleanAsync(CancellationToken testToken) {
    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (msgs, ct) => Task.CompletedTask,
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    await sut.FlushAndStopAsync(testToken);
  }

  /// <summary>Captures error-level messages so a dropped batch can be shown to be reported.</summary>
  private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<Whizbang.Core.Workers.SlidingWindowInboxBatchStrategy> {
    private readonly Lock _lock = new();
    private readonly List<string> _errors = [];

    public List<string> Errors {
      get { lock (_lock) { return [.. _errors]; } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error) {
        lock (_lock) { _errors.Add(formatter(state, exception)); }
      }
    }
  }
}
