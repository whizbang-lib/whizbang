// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
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
/// The coalescing outbox batch strategy (#2220 Part 2): writes from many streams are stored in one flush, a stream's
/// messages stay in order (one partition per stream, ordered by stream then message id within a flush), and a batch that
/// cannot be stored is reported one stream at a time. Every wait here ends on the size bound, a stop, or a timer the
/// test advances after it exists; none on real time.
/// </summary>
public class CoalescingOutboxBatchStrategyTests {
  private static OutboxMessage _message(Guid? stream) {
    var id = (Guid)TrackedGuid.New();
    return new OutboxMessage {
      MessageId = id,
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(id), JsonElement.Parse("{}"), []),
      EnvelopeType = "Envelope",
      MessageType = "Orders.OrderPlaced",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
      StreamId = stream,
    };
  }

  private static CoalescingOutboxOptions _options(int maxSize, int partitions = 1, int attempts = 5) => new() {
    MaxSize = maxSize,
    Partitions = partitions,
    MaxFlushAttempts = attempts,
    SlidingWindow = TimeSpan.FromMinutes(1),
    MaxWait = TimeSpan.FromMinutes(1),
  };

  private static FakeDbException _transient() =>
    FakeDbException.WithSqlState(null, message: "The connection pool has been exhausted", inner: new TimeoutException());

  private sealed class RecordingFlush(int expectedBatches) {
    private readonly int _expectedBatches = expectedBatches;
    public ConcurrentQueue<OutboxMessage[]> Batches { get; } = new();
    public TaskCompletionSource AllFlushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FlushAsync(OutboxMessage[] messages, CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      Batches.Enqueue(messages);
      if (Batches.Count == _expectedBatches) {
        AllFlushed.TrySetResult();
      }
      return Task.CompletedTask;
    }
  }

  [Test]
  [Timeout(60000)]
  public async Task WritesFromManyStreams_AreStoredInOneFlushAsync(CancellationToken cancellationToken) {
    var flush = new RecordingFlush(1);
    await using var sut = new CoalescingOutboxBatchStrategy(flush.FlushAsync, new FakeLogger<CoalescingOutboxBatchStrategy>(),
      _options(maxSize: 3), new FakeTimeProvider(), onFlushFailed: null, metrics: null);

    await sut.AppendAsync(_message(Guid.NewGuid()), cancellationToken);
    await sut.AppendAsync(_message(Guid.NewGuid()), cancellationToken);
    await sut.AppendAsync(_message(Guid.NewGuid()), cancellationToken);
    await flush.AllFlushed.Task.WaitAsync(cancellationToken);

    var batch = flush.Batches.Single();
    await Assert.That(batch.Length).IsEqualTo(3);
    await Assert.That(batch.Select(m => m.StreamId).Distinct().Count()).IsEqualTo(3);
    await Assert.That(sut.PartitionCount).IsEqualTo(1);
  }

  [Test]
  [Timeout(60000)]
  public async Task WithinAFlush_MessagesAreOrderedByStream_ThenByMessageIdAsync(CancellationToken cancellationToken) {
    var flush = new RecordingFlush(1);
    await using var sut = new CoalescingOutboxBatchStrategy(flush.FlushAsync, null, _options(maxSize: 4), new FakeTimeProvider(), null, null);
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();
    var a1 = _message(streamA);
    var a2 = _message(streamA);
    var b1 = _message(streamB);
    var b2 = _message(streamB);

    foreach (var message in new[] { b2, a2, a1, b1 }) {
      await sut.AppendAsync(message, cancellationToken);
    }
    await flush.AllFlushed.Task.WaitAsync(cancellationToken);

    var expected = new[] { a1, a2, b1, b2 }.OrderBy(m => m.StreamId).ThenBy(m => m.MessageId).Select(m => m.MessageId);
    await Assert.That(flush.Batches.Single().Select(m => m.MessageId)).IsEquivalentTo(expected,
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task AStream_AlwaysMapsToTheSamePartition_WithinRangeAsync() {
    var stream = Guid.NewGuid();
    var first = CoalescingOutboxBatchStrategy.PartitionOf(stream, 4);

    await Assert.That(CoalescingOutboxBatchStrategy.PartitionOf(stream, 4)).IsEqualTo(first);
    await Assert.That(first).IsBetween(0, 3);
    await Assert.That(Enumerable.Range(0, 200).Select(_ => CoalescingOutboxBatchStrategy.PartitionOf(Guid.NewGuid(), 4))
      .All(p => p is >= 0 and < 4)).IsTrue();
  }

  [Test]
  public async Task Stopping_FlushesAPartialBatch_RefusesLaterAppends_AndIsIdempotentAsync() {
    var flush = new RecordingFlush(1);
    var sut = new CoalescingOutboxBatchStrategy(flush.FlushAsync, null, _options(maxSize: 100, partitions: 2), new FakeTimeProvider(), null, null);
    var unstreamed = _message(null);
    await sut.AppendAsync(unstreamed);

    await sut.FlushAndStopAsync();
    await sut.FlushAndStopAsync();
    await sut.DisposeAsync();

    await Assert.That(flush.Batches.Single().Single().MessageId).IsEqualTo(unstreamed.MessageId);
    await Assert.That(async () => await sut.AppendAsync(_message(null))).Throws<ObjectDisposedException>();
  }

  [Test]
  public async Task ABatchThatCannotBeStored_IsReportedOneStreamAtATime_AndDeadLetteredAsync() {
    var failures = new ConcurrentQueue<OutboxFlushFailure>();
    var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
    var logger = new FakeLogger<CoalescingOutboxBatchStrategy>();
    var sut = new CoalescingOutboxBatchStrategy(
      (_, _) => throw new InvalidOperationException("not a database failure"),
      logger, _options(maxSize: 3), new FakeTimeProvider(),
      (failure, _) => {
        failures.Enqueue(failure);
        return Task.FromResult(true);
      },
      new WorkCoordinatorMetrics(new WhizbangMetrics(meterFactory: factory)));
    var streamA = Guid.NewGuid();
    await sut.AppendAsync(_message(streamA));
    await sut.AppendAsync(_message(streamA));
    await sut.AppendAsync(_message(null));

    await sut.FlushAndStopAsync();

    await Assert.That(failures.Count).IsEqualTo(2).Because("one record per stream: stream A and the messages with no stream");
    await Assert.That(failures.All(f => f.Cause == OutboxFlushFailureCause.NotRetryable)).IsTrue();
    await Assert.That(failures.Sum(f => f.Messages.Count)).IsEqualTo(3);
    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Information)).IsEqualTo(2);
  }

  [Test]
  [Timeout(60000)]
  [Arguments(true)]
  [Arguments(false)]
  public async Task ATransientFailure_IsRetried_ThenReportedLost_WhenNothingRecordsItAsync(bool withMetrics, CancellationToken cancellationToken) {
    var clock = new TimerAdvancingClock(TimeSpan.FromMinutes(1));
    var attempts = 0;
    var reported = new TaskCompletionSource<OutboxFlushFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
    var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
    var logger = new FakeLogger<CoalescingOutboxBatchStrategy>();
    var sut = new CoalescingOutboxBatchStrategy(
      (_, _) => {
        Interlocked.Increment(ref attempts);
        return Task.FromException(_transient());
      },
      logger, _options(maxSize: 1, attempts: 3), clock,
      (failure, _) => {
        reported.TrySetResult(failure);
        return Task.FromResult(false);
      },
      // A host with metrics off retries exactly as one with them on; it only records no retry count.
      withMetrics ? new WorkCoordinatorMetrics(new WhizbangMetrics(meterFactory: factory)) : null);
    await sut.AppendAsync(_message(Guid.NewGuid()), cancellationToken);

    await clock.AdvanceTimersUntilAsync(reported.Task, cancellationToken);
    // The stop waits for the drain, so the report the callback started has been logged once it returns.
    await sut.FlushAndStopAsync(cancellationToken);

    await Assert.That((await reported.Task).Cause).IsEqualTo(OutboxFlushFailureCause.RetriesExhausted);
    await Assert.That(attempts).IsEqualTo(3);
    var messages = logger.Collector.GetSnapshot().Select(r => r.Message).ToList();
    await Assert.That(messages.Count(m => m.Contains("retrying in", StringComparison.Ordinal))).IsEqualTo(2);
    await Assert.That(messages.Count(m => m.Contains("are lost", StringComparison.Ordinal))).IsEqualTo(1);
  }

  [Test]
  [Timeout(60000)]
  public async Task AFailureThatCannotBeRecorded_OrHasNoRecorder_IsLoggedAsLostAsync(CancellationToken cancellationToken) {
    var throwing = new FakeLogger<CoalescingOutboxBatchStrategy>();
    var unrecorded = new FakeLogger<CoalescingOutboxBatchStrategy>();
    var withThrowingRecorder = new CoalescingOutboxBatchStrategy(
      (_, _) => throw new InvalidOperationException("refused"), throwing, _options(maxSize: 1), new FakeTimeProvider(),
      (_, _) => throw new ObjectDisposedException("provider"), null);
    var withNoRecorder = new CoalescingOutboxBatchStrategy(
      (_, _) => throw new InvalidOperationException("refused"), unrecorded, _options(maxSize: 1), new FakeTimeProvider(), null, null);

    await withThrowingRecorder.AppendAsync(_message(Guid.NewGuid()), cancellationToken);
    await withNoRecorder.AppendAsync(_message(Guid.NewGuid()), cancellationToken);
    await withThrowingRecorder.FlushAndStopAsync(cancellationToken);
    await withNoRecorder.FlushAndStopAsync(cancellationToken);

    await Assert.That(throwing.Collector.GetSnapshot().Count(r => r.Message.StartsWith("Recording", StringComparison.Ordinal))).IsEqualTo(1);
    await Assert.That(throwing.Collector.GetSnapshot().Count(r => r.Message.Contains("are lost", StringComparison.Ordinal))).IsEqualTo(1);
    await Assert.That(unrecorded.Collector.GetSnapshot().Count(r => r.Message.Contains("are lost", StringComparison.Ordinal))).IsEqualTo(1);
  }

  [Test]
  [Timeout(60000)]
  public async Task AStopPastItsDeadline_AbandonsWhatIsStillBuffered_AndReportsItAsync(CancellationToken cancellationToken) {
    var failures = new ConcurrentQueue<OutboxFlushFailure>();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var sut = new CoalescingOutboxBatchStrategy(
      async (_, ct) => {
        entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
      },
      null, _options(maxSize: 1), new FakeTimeProvider(),
      (failure, _) => {
        failures.Enqueue(failure);
        return Task.FromResult(true);
      },
      null);
    var stream = Guid.NewGuid();
    await sut.AppendAsync(_message(stream), cancellationToken);
    await entered.Task.WaitAsync(cancellationToken);
    await sut.AppendAsync(_message(stream), cancellationToken);
    await sut.AppendAsync(_message(stream), cancellationToken);

    await sut.FlushAndStopAsync(new CancellationToken(canceled: true));
    await sut.WhenDrainsStoppedForTests();

    await Assert.That(failures.Single().Cause).IsEqualTo(OutboxFlushFailureCause.AbandonedAtShutdown);
    await Assert.That(failures.Single().Messages.Count).IsEqualTo(3);
  }

  [Test]
  public async Task WithNoOptionsClockOrLogger_TheDefaultsApplyAsync() {
    var flush = new RecordingFlush(1);
    var sut = new CoalescingOutboxBatchStrategy(flush.FlushAsync, null, null, null, null, null);

    await Assert.That(sut.PartitionCount).IsEqualTo(new CoalescingOutboxOptions().Partitions);
    await sut.FlushAndStopAsync();
  }

  [Test]
  public async Task TheConstructor_RefusesAMissingFlushOrImpossibleOptions_AndAppendRefusesNullAsync() {
    await Assert.That(() => new CoalescingOutboxBatchStrategy(null!, null, null, null, null, null)).Throws<ArgumentNullException>();
    await Assert.That(() => new CoalescingOutboxBatchStrategy((_, _) => Task.CompletedTask, null, new CoalescingOutboxOptions { Partitions = 0 }, null, null, null))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new CoalescingOutboxBatchStrategy((_, _) => Task.CompletedTask, null, new CoalescingOutboxOptions { MaxSize = 0 }, null, null, null))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new CoalescingOutboxBatchStrategy((_, _) => Task.CompletedTask, null, new CoalescingOutboxOptions { MaxFlushAttempts = 0 }, null, null, null))
      .Throws<ArgumentOutOfRangeException>();
    await using var sut = new CoalescingOutboxBatchStrategy((_, _) => Task.CompletedTask, null, _options(1), new FakeTimeProvider(), null, null);
    await Assert.That(async () => await sut.AppendAsync(null!)).Throws<ArgumentNullException>();
  }
}
