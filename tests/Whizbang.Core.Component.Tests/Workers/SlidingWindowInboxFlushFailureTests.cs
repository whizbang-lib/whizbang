// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
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
    var (metrics, meter) = _metrics();
    using var recorded = new MetricAssertionHelper(meter);

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
      timeProvider: clock,
      onFlushFailed: null,
      metrics: metrics);

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
    var retries = recorded.GetByName("whizbang.work_coordinator.inbox_flush.retries").Where(m => m.Tags.ContainsKey(WorkCoordinatorMetrics.REASON_TAG)).ToList();
    await Assert.That(retries.Sum(m => m.Value)).IsEqualTo((double)failures);
    await Assert.That(retries.All(m => m.Tags[WorkCoordinatorMetrics.REASON_TAG] == TransientDatabaseFailure.INSUFFICIENT_RESOURCES)).IsTrue()
      .Because("each retry is counted under the kind of transient failure it retried");
  }

  /// <summary>
  /// A store that fails transiently on every attempt is tried <see cref="SlidingWindowInboxOptions.MaxFlushAttempts"/>
  /// times and then handed to the failure callback, once, with the whole batch and its last error; the messages it
  /// dead-lettered are counted as such, and the error log names every message id and how to recover them.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task FlushFailsTransientlyOnEveryAttempt_DeadLettersTheBatchAfterMaxFlushAttemptsAsync(
      CancellationToken cancellationToken) {
    var attempts = 0;
    var failures = new ConcurrentQueue<InboxFlushFailure>();
    var clock = new TimerAdvancingClock(_idleSweep);
    var log = new CapturingLog();
    var (metrics, meter) = _metrics();
    using var recorded = new MetricAssertionHelper(meter);
    var stream = (Guid)TrackedGuid.New();

    var sut = new SlidingWindowInboxBatchStrategy(
      flush: (_, _) => {
        Interlocked.Increment(ref attempts);
        return Task.FromException(_poolExhausted());
      },
      logger: log,
      options: _flushEveryMessageAtOnce() with { MaxFlushAttempts = 3 },
      timeProvider: clock,
      onFlushFailed: (failure, _) => {
        failures.Enqueue(failure);
        return Task.FromResult(true);
      },
      metrics: metrics);

    var message = _make(stream);
    await sut.AppendAsync(message, cancellationToken);
    await clock.AdvanceTimersUntilAsync(sut.FlushAndStopAsync(CancellationToken.None), cancellationToken);

    await Assert.That(attempts).IsEqualTo(3);
    await Assert.That(failures).Count().IsEqualTo(1);
    var reported = failures.Single();
    await Assert.That(reported.Cause).IsEqualTo(InboxFlushFailureCause.RetriesExhausted);
    await Assert.That(reported.Attempts).IsEqualTo(3);
    await Assert.That(reported.StreamId).IsEqualTo(stream);
    await Assert.That(reported.Messages.Select(m => m.MessageId)).IsEquivalentTo([message.MessageId]);
    await Assert.That(reported.Error).IsTypeOf<FakeDbException>();
    var failed = recorded.GetByName("whizbang.work_coordinator.inbox_flush.failed_messages").Where(m => m.Tags.ContainsKey(WorkCoordinatorMetrics.CAUSE_TAG)).ToList();
    await Assert.That(failed).Count().IsEqualTo(1);
    await Assert.That(failed[0].Value).IsEqualTo(1);
    await Assert.That(failed[0].Tags[WorkCoordinatorMetrics.CAUSE_TAG]).IsEqualTo(nameof(InboxFlushFailureCause.RetriesExhausted));
    await Assert.That(failed[0].Tags[WorkCoordinatorMetrics.OUTCOME_TAG]).IsEqualTo(WorkCoordinatorMetrics.OUTCOME_DEAD_LETTERED);
    var error = log.Errors.Single();
    await Assert.That(error).Contains(message.MessageId.ToString())
      .Because("the error log names every message the batch held");
    await Assert.That(error).Contains("wh_dead_letters").And.Contains("recover")
      .Because("it says where the messages went and how to get them back");
    await Assert.That(log.Warnings).Count().IsEqualTo(2)
      .Because("each retry is logged once: attempts one and two, before the third gave the batch up");
  }

  /// <summary>
  /// An error a retry cannot fix is not retried: the batch goes to the failure callback after one attempt. A callback
  /// that could not dead-letter it leaves the messages counted as not recorded and logged as lost, with their ids.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task FlushFailsWithANonTransientError_IsReportedAtOnceAndLoggedAsLostWhenNotDeadLetteredAsync(
      CancellationToken cancellationToken) {
    var attempts = 0;
    var failures = new ConcurrentQueue<InboxFlushFailure>();
    var log = new CapturingLog();
    var (metrics, meter) = _metrics();
    using var recorded = new MetricAssertionHelper(meter);

    var sut = new SlidingWindowInboxBatchStrategy(
      flush: (_, _) => {
        Interlocked.Increment(ref attempts);
        return Task.FromException(new InvalidOperationException("the stream id is empty"));
      },
      logger: log,
      options: _flushEveryMessageAtOnce(),
      timeProvider: new TimerAdvancingClock(_idleSweep),
      onFlushFailed: (failure, _) => {
        failures.Enqueue(failure);
        return Task.FromResult(false);
      },
      metrics: metrics);

    var message = _make((Guid)TrackedGuid.New());
    await sut.AppendAsync(message, cancellationToken);
    await sut.FlushAndStopAsync(CancellationToken.None).WaitAsync(cancellationToken);

    await Assert.That(attempts).IsEqualTo(1).Because("a failure a retry cannot fix is not retried");
    var reported = failures.Single();
    await Assert.That(reported.Cause).IsEqualTo(InboxFlushFailureCause.NotRetryable);
    await Assert.That(reported.Attempts).IsEqualTo(1);
    var failed = recorded.GetByName("whizbang.work_coordinator.inbox_flush.failed_messages").Single(m => m.Tags.ContainsKey(WorkCoordinatorMetrics.CAUSE_TAG));
    await Assert.That(failed.Tags[WorkCoordinatorMetrics.OUTCOME_TAG]).IsEqualTo(WorkCoordinatorMetrics.OUTCOME_NOT_RECORDED);
    var error = log.Errors.Single();
    await Assert.That(error).Contains(message.MessageId.ToString());
    await Assert.That(error).Contains("lost");
  }

  /// <summary>
  /// A failure callback that throws does not end the stream's worker: the failure to record is logged, the batch is
  /// logged as lost with its ids, and the stream's next batch is still stored.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task FailureCallbackThrows_TheBatchIsLoggedAsLostAndTheStreamKeepsFlushingAsync(
      CancellationToken cancellationToken) {
    var stored = new ConcurrentQueue<Guid>();
    var log = new CapturingLog();
    var stream = (Guid)TrackedGuid.New();
    var first = _make(stream);
    var second = _make(stream);

    var sut = new SlidingWindowInboxBatchStrategy(
      flush: (messages, _) => {
        if (messages[0].MessageId == first.MessageId) {
          return Task.FromException(new InvalidOperationException("refused"));
        }
        stored.Enqueue(messages[0].MessageId);
        return Task.CompletedTask;
      },
      logger: log,
      options: _flushEveryMessageAtOnce(),
      timeProvider: new TimerAdvancingClock(_idleSweep),
      onFlushFailed: (_, _) => Task.FromException<bool>(new ObjectDisposedException("IServiceProvider")),
      metrics: null);

    await sut.AppendAsync(first, cancellationToken);
    await sut.AppendAsync(second, cancellationToken);
    await sut.FlushAndStopAsync(CancellationToken.None).WaitAsync(cancellationToken);

    await Assert.That(stored).IsEquivalentTo([second.MessageId]);
    await Assert.That(log.Errors).Count().IsEqualTo(2);
    await Assert.That(log.Errors.Any(e => e.Contains("Recording", StringComparison.Ordinal))).IsTrue();
    await Assert.That(log.Errors.Any(e => e.Contains(first.MessageId.ToString(), StringComparison.Ordinal) && e.Contains("lost", StringComparison.Ordinal))).IsTrue();
  }

  /// <summary>
  /// A stop past its deadline abandons the batch being stored and the stream's batches still buffered behind it, and
  /// reports them all, once, as abandoned at shutdown: no attempt counted against them, no error of their own.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task CanceledStop_AbandonsTheBatchInFlightAndTheOnesBufferedBehindIt_ReportsThemOnceAsync(
      CancellationToken cancellationToken) {
    var flushEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var failures = new ConcurrentQueue<InboxFlushFailure>();
    var abandonedReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stream = (Guid)TrackedGuid.New();

    var sut = new SlidingWindowInboxBatchStrategy(
      flush: async (_, ct) => {
        flushEntered.TrySetResult();
        // Completes only when the stop cancels the strategy's own token: no competing path, no race.
        await Task.Delay(Timeout.Infinite, ct);
      },
      logger: NullLogger<SlidingWindowInboxBatchStrategy>.Instance,
      options: _flushEveryMessageAtOnce(),
      timeProvider: new TimerAdvancingClock(_idleSweep),
      onFlushFailed: (failure, _) => {
        failures.Enqueue(failure);
        abandonedReported.TrySetResult();
        return Task.FromResult(true);
      },
      metrics: null);

    var inFlight = _make(stream);
    var buffered = _make(stream);
    await sut.AppendAsync(inFlight, cancellationToken);
    await flushEntered.Task.WaitAsync(cancellationToken);
    await sut.AppendAsync(buffered, cancellationToken);

    using var deadline = new CancellationTokenSource();
    await deadline.CancelAsync();
    // The stop returns at once; the worker reports what it abandoned on its own, and the test waits for that report.
    await sut.FlushAndStopAsync(deadline.Token).WaitAsync(cancellationToken);
    await abandonedReported.Task.WaitAsync(cancellationToken);

    var reported = failures.Single();
    await Assert.That(reported.Cause).IsEqualTo(InboxFlushFailureCause.AbandonedAtShutdown);
    await Assert.That(reported.Messages.Select(m => m.MessageId)).IsEquivalentTo([inFlight.MessageId, buffered.MessageId]);
    await Assert.That(reported.Attempts).IsEqualTo(0);
    await Assert.That(reported.Error).IsNull();
  }

  /// <summary>
  /// A stop past its deadline that lands while a batch waits to be retried abandons it and reports it.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task CanceledStop_DuringRetryBackoff_AbandonsAndReportsTheBatchAsync(CancellationToken cancellationToken) {
    var failures = new ConcurrentQueue<InboxFlushFailure>();
    var abandonedReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = new TimerAdvancingClock(_idleSweep);

    var sut = new SlidingWindowInboxBatchStrategy(
      flush: (_, _) => Task.FromException(_poolExhausted()),
      logger: NullLogger<SlidingWindowInboxBatchStrategy>.Instance,
      options: _flushEveryMessageAtOnce(),
      timeProvider: clock,
      onFlushFailed: (failure, _) => {
        failures.Enqueue(failure);
        abandonedReported.TrySetResult();
        return Task.FromResult(true);
      },
      metrics: null);

    var message = _make((Guid)TrackedGuid.New());
    await sut.AppendAsync(message, cancellationToken);
    // The retry's wait exists: the first attempt failed and the worker is waiting to try again.
    await clock.Created(1).WaitAsync(cancellationToken);

    using var deadline = new CancellationTokenSource();
    await deadline.CancelAsync();
    // The stop returns at once; the worker reports what it abandoned on its own, and the test waits for that report.
    await sut.FlushAndStopAsync(deadline.Token).WaitAsync(cancellationToken);
    await abandonedReported.Task.WaitAsync(cancellationToken);

    var reported = failures.Single();
    await Assert.That(reported.Cause).IsEqualTo(InboxFlushFailureCause.AbandonedAtShutdown);
    await Assert.That(reported.Messages.Select(m => m.MessageId)).IsEquivalentTo([message.MessageId]);
  }

  /// <summary>The backoff doubles from <see cref="SlidingWindowInboxOptions.FlushRetryBackoff"/> and stops at the maximum.</summary>
  [Test]
  public async Task FlushRetryBackoff_DoublesUpToTheMaximumAsync() {
    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (_, _) => Task.CompletedTask,
      logger: NullLogger<SlidingWindowInboxBatchStrategy>.Instance,
      options: new SlidingWindowInboxOptions {
        FlushRetryBackoff = TimeSpan.FromMilliseconds(100),
        FlushRetryMaxBackoff = TimeSpan.FromMilliseconds(350),
      },
      timeProvider: new TimerAdvancingClock(_idleSweep));

    await Assert.That(sut.RetryBackoff(1)).IsEqualTo(TimeSpan.FromMilliseconds(100));
    await Assert.That(sut.RetryBackoff(2)).IsEqualTo(TimeSpan.FromMilliseconds(200));
    await Assert.That(sut.RetryBackoff(3)).IsEqualTo(TimeSpan.FromMilliseconds(350));
    await Assert.That(sut.RetryBackoff(60)).IsEqualTo(TimeSpan.FromMilliseconds(350));
  }

  /// <summary>Fewer than one attempt would store nothing, so it is refused when the strategy is built.</summary>
  [Test]
  public async Task Constructor_MaxFlushAttemptsBelowOne_ThrowsAsync() {
    await Assert.That(() => {
      _ = new SlidingWindowInboxBatchStrategy(
        flush: (_, _) => Task.CompletedTask,
        logger: NullLogger<SlidingWindowInboxBatchStrategy>.Instance,
        options: new SlidingWindowInboxOptions { MaxFlushAttempts = 0 },
        timeProvider: new TimerAdvancingClock(_idleSweep));
    }).Throws<ArgumentOutOfRangeException>();
  }

  // Npgsql's shape for an exhausted pool: a database exception whose inner exception is the timeout.
  private static FakeDbException _poolExhausted() =>
    FakeDbException.WithSqlState(null,
      message: "The connection pool has been exhausted, either raise 'Max Pool Size' (currently 100) or 'Timeout' in your connection string.",
      inner: new TimeoutException());

  private static (WorkCoordinatorMetrics Metrics, Meter Meter) _metrics() {
    var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(meterFactory: factory));
    return (metrics, factory.Create(WorkCoordinatorMetrics.METER_NAME));
  }

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

  /// <summary>Keeps the strategy's warnings and errors, formatted.</summary>
  private sealed class CapturingLog : ILogger<SlidingWindowInboxBatchStrategy> {
    private readonly ConcurrentQueue<(LogLevel Level, string Text)> _entries = new();

    public IReadOnlyList<string> Errors => [.. _entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Text)];

    public IReadOnlyList<string> Warnings => [.. _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Text)];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      _entries.Enqueue((logLevel, formatter(state, exception)));
  }
}
