// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1871: a per-stream flush of <see cref="SlidingWindowOutboxBatchStrategy"/> that fails is dropped. The drain worker
/// logs "Dispatcher re-emission will recover" and moves on, but nothing re-emits a message the batcher accepted, so the
/// message is gone with no row, no dead letter and no failure seen by the code that appended it.
/// </summary>
/// <remarks>
/// The reported shape: one message on each of 126 streams, appended together, so 126 drain workers flush at once
/// against a remote database. Each flush takes a connection for its own scope; the ones that cannot get one fail
/// (pool exhausted, or the server out of connection slots) and a random-looking tenth of the messages never lands.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowOutboxBatchStrategy.cs</code-under-test>
public class SlidingWindowOutboxFlushFailureTests {
  private const int STREAMS = 126;

  // Npgsql's default Max Pool Size: the flushes beyond it are the ones that cannot get a connection.
  private const int POOL_SIZE = 100;

  /// <summary>
  /// 126 streams flush at once through a pool of 100 connections. Every flush that gets a connection holds it until
  /// all 126 have tried, so exactly 26 find the pool exhausted on their first try; the failure is transient, a
  /// second try finds a free connection. Every message the batcher accepted has to be stored.
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
    var everyStreamTried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    void countFirstAttempt() {
      if (Interlocked.Increment(ref firstAttempts) == STREAMS) {
        everyStreamTried.TrySetResult();
      }
    }

    var sut = new SlidingWindowOutboxBatchStrategy(
      flush: async (messages, _) => {
        var stream = messages[0].StreamId!.Value;
        var first = attemptsByStream.AddOrUpdate(stream, 1, static (_, n) => n + 1) == 1;
        if (!await pool.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) {
          Interlocked.Increment(ref rejected);
          if (first) {
            countFirstAttempt();
          }
          throw new TimeoutException(
            "The connection pool has been exhausted, either raise 'Max Pool Size' (currently 100) or 'Timeout' in your connection string.");
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
        }
      },
      logger: NullLogger<SlidingWindowOutboxBatchStrategy>.Instance,
      options: _flushEveryMessageAtOnce(),
      timeProvider: new FakeTimeProvider());

    var appended = new List<Guid>(STREAMS);
    for (var i = 0; i < STREAMS; i++) {
      var message = _make((Guid)TrackedGuid.New());
      appended.Add(message.MessageId);
      await sut.AppendAsync(message, cancellationToken);
    }

    await everyStreamTried.Task.WaitAsync(cancellationToken);
    // The stop a host makes: every buffer completed and every drain worker awaited.
    await sut.FlushAndStopAsync(CancellationToken.None).WaitAsync(cancellationToken);

    await Assert.That(rejected).IsGreaterThanOrEqualTo(STREAMS - POOL_SIZE)
      .Because("the arrangement has to really exhaust the pool, or the assertion below would hold vacuously");
    var missing = appended.Where(id => !stored.ContainsKey(id)).ToList();
    await Assert.That(missing).IsEmpty()
      .Because($"{missing.Count} of {STREAMS} accepted messages were never stored: a flush that failed on a transient "
             + "pool exhaustion was dropped with a promise of re-emission that nothing keeps (#1871)");
  }

  /// <summary>
  /// The same loss without concurrency: the 10th, 20th, ... stream's first flush fails once (12 of 126, inside the
  /// reported 7-13%), a second try would succeed. Every message the batcher accepted has to be stored.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task FlushFailsOnceForSomeStreams_EveryAcceptedMessageIsStoredAsync(CancellationToken cancellationToken) {
    var failOnce = new ConcurrentDictionary<Guid, byte>();
    var stored = new ConcurrentDictionary<Guid, byte>();
    var failures = 0;

    var sut = new SlidingWindowOutboxBatchStrategy(
      flush: (messages, flushToken) => {
        var stream = messages[0].StreamId!.Value;
        if (failOnce.TryRemove(stream, out _)) {
          Interlocked.Increment(ref failures);
          return Task.FromException(new TimeoutException("Timeout during reading attempt"));
        }
        foreach (var message in messages) {
          stored.TryAdd(message.MessageId, 0);
        }
        return Task.CompletedTask;
      },
      logger: NullLogger<SlidingWindowOutboxBatchStrategy>.Instance,
      options: _flushEveryMessageAtOnce(),
      timeProvider: new FakeTimeProvider());

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

    await sut.FlushAndStopAsync(CancellationToken.None).WaitAsync(cancellationToken);

    await Assert.That(failures).IsEqualTo(STREAMS / 10)
      .Because("every chosen stream's first flush has to really fail, or the assertion below would hold vacuously");
    var missing = appended.Where(id => !stored.ContainsKey(id)).ToList();
    await Assert.That(missing).IsEmpty()
      .Because($"{missing.Count} of {STREAMS} accepted messages were never stored and no caller saw a failure (#1871)");
  }

  private static SlidingWindowOutboxOptions _flushEveryMessageAtOnce() => new() {
    // One message per batch: each drain worker yields on the size bound without consulting the clock, so all 126
    // flushes start as soon as their message is appended, with no fake-timer choreography.
    MaxSize = 1,
    SlidingWindow = TimeSpan.FromMilliseconds(50),
    MaxWait = TimeSpan.FromSeconds(1),
    IdleSweepInterval = TimeSpan.FromMinutes(5),
    IdleEvictionWindow = TimeSpan.FromSeconds(30),
  };

  private static OutboxMessage _make(Guid streamId) {
    var messageId = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonElement.Parse("{}"), []);
    return new OutboxMessage {
      MessageId = messageId,
      StreamId = streamId,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      IsEvent = true,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
    };
  }
}
