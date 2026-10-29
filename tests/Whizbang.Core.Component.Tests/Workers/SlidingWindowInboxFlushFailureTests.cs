// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1885: a per-stream flush of <see cref="SlidingWindowInboxBatchStrategy"/> that failed was logged ("Transport
/// redelivery will recover") and dropped. <see cref="IInboxBatchStrategy.AppendAsync"/> returns once the message is
/// buffered, so whoever appended it may already have settled it with its broker, and then nothing delivers it again:
/// the message is gone with no inbox row and no dead letter. The transient failures a burst of per-stream flushes
/// meets (an exhausted pool, the server out of connection slots) are the common case, exactly as for the outbox
/// batcher (#1871).
/// </summary>
/// <remarks>
/// The same shape as the outbox reproduction: one message on each of 126 streams, so 126 drain workers flush at once.
/// Every wait the strategy schedules runs on a <see cref="TimerAdvancingClock"/>, advanced by the test.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowInboxBatchStrategy.cs</code-under-test>
public class SlidingWindowInboxFlushFailureTests {
  private const int STREAMS = 126;

  // Npgsql's default Max Pool Size: the flushes beyond it are the ones that cannot get a connection.
  private const int POOL_SIZE = 100;

  private static readonly TimeSpan _idleSweep = TimeSpan.FromMinutes(5);

  /// <summary>
  /// 126 streams flush at once through a pool of 100 connections. Every flush that gets a connection holds it until
  /// all 126 have tried, so exactly 26 find the pool exhausted on their first try. The retries run only once every
  /// holder has given its connection back, so each finds a free one. Every message the batcher accepted has to be
  /// stored.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task ConcurrentPerStreamFlushes_SomeFindThePoolExhausted_EveryAcceptedMessageIsStoredAsync(
      CancellationToken cancellationToken) {
    var pool = new SemaphoreSlim(POOL_SIZE, POOL_SIZE);
    var attemptsByStream = new ConcurrentDictionary<Guid, int>();
    var stored = new ConcurrentDictionary<Guid, byte>();
    var firstAttempts = 0;
    var rejected = 0;
    var released = 0;
    var everyStreamTried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var everyHolderReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = new TimerAdvancingClock(_idleSweep);

    void countFirstAttempt() {
      if (Interlocked.Increment(ref firstAttempts) == STREAMS) {
        everyStreamTried.TrySetResult();
      }
    }

    var sut = new SlidingWindowInboxBatchStrategy(
      flush: async (messages, _) => {
        var stream = messages[0].StreamId!.Value;
        var first = attemptsByStream.AddOrUpdate(stream, 1, static (_, n) => n + 1) == 1;
        if (!await pool.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) {
          Interlocked.Increment(ref rejected);
          if (first) {
            countFirstAttempt();
          }
          throw _poolExhausted();
        }
        try {
          if (first) {
            countFirstAttempt();
            // Hold the connection until every stream has tried: the 26 beyond the pool are certain to find it full.
            await everyStreamTried.Task.ConfigureAwait(false);
          }
          foreach (var message in messages) {
            stored.TryAdd(message.MessageId, 0);
          }
        } finally {
          pool.Release();
          if (first && Interlocked.Increment(ref released) == POOL_SIZE) {
            everyHolderReleased.TrySetResult();
          }
        }
      },
      logger: NullLogger<SlidingWindowInboxBatchStrategy>.Instance,
      options: _flushEveryMessageAtOnce(),
      timeProvider: clock);

    var appended = new List<Guid>(STREAMS);
    for (var i = 0; i < STREAMS; i++) {
      var message = _make((Guid)TrackedGuid.New());
      appended.Add(message.MessageId);
      await sut.AppendAsync(message, cancellationToken);
    }

    // No retry is let through before every holder has given its connection back, so none can find the pool full.
    await everyHolderReleased.Task.WaitAsync(cancellationToken);
    // The stop a host makes: every buffer completed and every drain worker awaited, its retries' waits let elapse.
    await clock.AdvanceTimersUntilAsync(sut.FlushAndStopAsync(CancellationToken.None), cancellationToken);

    await Assert.That(rejected).IsEqualTo(STREAMS - POOL_SIZE)
      .Because("the arrangement has to really exhaust the pool, or the assertion below would hold vacuously");
    var missing = appended.Where(id => !stored.ContainsKey(id)).ToList();
    await Assert.That(missing).IsEmpty()
      .Because($"{missing.Count} of {STREAMS} accepted inbox messages were never stored: a flush that failed on a "
             + "transient pool exhaustion has to be retried, not dropped (#1885)");
  }

  /// <summary>
  /// The same loss without concurrency: the 10th, 20th, ... stream's first flush fails once with the server out of
  /// connection slots (SQLSTATE 53300), and a second try succeeds. Every message the batcher accepted has to be stored.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task FlushFailsOnceForSomeStreams_EveryAcceptedMessageIsStoredAsync(CancellationToken cancellationToken) {
    var failOnce = new ConcurrentDictionary<Guid, byte>();
    var stored = new ConcurrentDictionary<Guid, byte>();
    var failures = 0;
    var clock = new TimerAdvancingClock(_idleSweep);

    var sut = new SlidingWindowInboxBatchStrategy(
      flush: (messages, flushToken) => {
        var stream = messages[0].StreamId!.Value;
        if (failOnce.TryRemove(stream, out _)) {
          Interlocked.Increment(ref failures);
          return Task.FromException(FakeDbException.WithSqlState("53300", message: "too many clients already"));
        }
        foreach (var message in messages) {
          stored.TryAdd(message.MessageId, 0);
        }
        return Task.CompletedTask;
      },
      logger: NullLogger<SlidingWindowInboxBatchStrategy>.Instance,
      options: _flushEveryMessageAtOnce(),
      timeProvider: clock);

    var appended = new List<Guid>(STREAMS);
    for (var i = 1; i <= STREAMS; i++) {
      var stream = (Guid)TrackedGuid.New();
      if (i % 10 == 0) {
        failOnce.TryAdd(stream, 0);
      }
      var message = _make(stream);
      appended.Add(message.MessageId);
      await sut.AppendAsync(message, cancellationToken);
    }

    await clock.AdvanceTimersUntilAsync(sut.FlushAndStopAsync(CancellationToken.None), cancellationToken);

    await Assert.That(failures).IsEqualTo(STREAMS / 10)
      .Because("every chosen stream's first flush has to really fail, or the assertion below would hold vacuously");
    var missing = appended.Where(id => !stored.ContainsKey(id)).ToList();
    await Assert.That(missing).IsEmpty()
      .Because($"{missing.Count} of {STREAMS} accepted inbox messages were never stored and nobody saw a failure (#1885)");
  }

  // Npgsql's shape for an exhausted pool: a database exception whose inner exception is the timeout.
  private static FakeDbException _poolExhausted() =>
    FakeDbException.WithSqlState(null,
      message: "The connection pool has been exhausted, either raise 'Max Pool Size' (currently 100) or 'Timeout' in your connection string.",
      inner: new TimeoutException());

  private static SlidingWindowInboxOptions _flushEveryMessageAtOnce() => new() {
    // One message per batch: each drain worker yields on the size bound without consulting the clock, so all 126
    // flushes start as soon as their message is appended, with no window timers.
    MaxSize = 1,
    SlidingWindow = TimeSpan.FromMilliseconds(50),
    MaxWait = TimeSpan.FromSeconds(1),
    IdleSweepInterval = _idleSweep,
    IdleEvictionWindow = TimeSpan.FromSeconds(30),
  };

  private static InboxMessage _make(Guid streamId) {
    var messageId = (Guid)TrackedGuid.New();
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "test",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonElement.Parse("{}"), []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
    };
  }
}
