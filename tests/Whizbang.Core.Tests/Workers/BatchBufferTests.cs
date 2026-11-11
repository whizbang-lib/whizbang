// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The process-wide bound on what the batch strategies hold in memory (#2220): an append takes room, waits while there
/// is none, and is admitted when a flush gives room back. Every wait here ends on a release or a cancellation the test
/// performs, never on time.
/// </summary>
public class BatchBufferTests {
  private static readonly BatchAppendContext _standard = BatchAppendContext.Anonymous("A test append");

  private static BatchBufferLimits _limits(int messages = 0, long? bytes = 0, int gateMax = 0, int gateReserve = 0, double ceiling = 2) =>
    BatchBufferLimits.Resolve(
      new BatchBufferOptions { MaxBufferedMessages = messages, MaxBufferedBytes = bytes, TransactionCeilingMultiplier = ceiling },
      totalAvailableMemoryBytes: 1_000_000, gateMaxConcurrent: gateMax, gateInteractiveReserve: gateReserve);

  private static BatchBuffer _buffer(BatchBufferLimits limits, FakeLogger<BatchBuffer>? logger = null,
      WorkCoordinatorMetrics? metrics = null, TimeProvider? time = null) =>
    new(limits, logger ?? new FakeLogger<BatchBuffer>(), metrics, time ?? new FakeTimeProvider());

  private static ValueTask _admit(BatchBuffer buffer, object message, long bytes = 1, bool interactive = false,
      BatchAppendContext? context = null, CancellationToken cancellationToken = default) =>
    buffer.AdmitAsync(message, bytes, interactive, context ?? _standard, cancellationToken);

  [Test]
  public async Task UnderTheLimits_AnAppendIsAdmittedAtOnceAsync() {
    var buffer = _buffer(_limits(messages: 2, bytes: 100));

    var admitted = _admit(buffer, new object(), bytes: 40).AsTask();

    await Assert.That(admitted.IsCompletedSuccessfully).IsTrue();
    await Assert.That(buffer.BufferedMessages).IsEqualTo(1);
    await Assert.That(buffer.BufferedBytes).IsEqualTo(40);
  }

  [Test]
  public async Task AtTheMessageLimit_AnAppendWaits_UntilAFlushReleasesRoomAsync() {
    var buffer = _buffer(_limits(messages: 2));
    var first = new object();
    await _admit(buffer, first);
    await _admit(buffer, new object());

    var third = _admit(buffer, new object()).AsTask();

    await Assert.That(third.IsCompleted).IsFalse();
    await Assert.That(buffer.WaitingAppends).IsEqualTo(1);
    buffer.Release([first]);
    await third;
    await Assert.That(buffer.BufferedMessages).IsEqualTo(2);
    await Assert.That(buffer.WaitingAppends).IsEqualTo(0);
  }

  [Test]
  public async Task AtTheByteLimit_AnAppendWaits_EvenWithMessagesToSpareAsync() {
    var buffer = _buffer(_limits(messages: 100, bytes: 100));
    var first = new object();
    await _admit(buffer, first, bytes: 60);

    var second = _admit(buffer, new object(), bytes: 50).AsTask();

    await Assert.That(second.IsCompleted).IsFalse();
    buffer.ReleaseOne(first);
    await second;
    await Assert.That(buffer.BufferedBytes).IsEqualTo(50);
  }

  [Test]
  public async Task AMessageLargerThanTheByteLimit_IsAdmittedAloneIntoAnEmptyBufferAsync() {
    var buffer = _buffer(_limits(bytes: 100));
    var large = new object();

    var admitted = _admit(buffer, large, bytes: 500).AsTask();
    var next = _admit(buffer, new object(), bytes: 1).AsTask();

    await Assert.That(admitted.IsCompletedSuccessfully).IsTrue();
    await Assert.That(next.IsCompleted).IsFalse();
    buffer.ReleaseOne(large);
    await next;
  }

  [Test]
  public async Task WithNoLimits_NothingWaitsAsync() {
    var buffer = _buffer(_limits(messages: 0, bytes: 0));

    for (var i = 0; i < 1000; i++) {
      await Assert.That(_admit(buffer, new object(), bytes: 1_000_000).AsTask().IsCompletedSuccessfully).IsTrue();
    }
  }

  [Test]
  public async Task AStandardCaller_CannotUseTheInteractiveReserve_AndAnInteractiveCallerCanAsync() {
    // A gate of 10 permits reserving 2: a fifth of each limit is held back for interactive callers.
    var buffer = _buffer(_limits(messages: 10, gateMax: 10, gateReserve: 2));
    for (var i = 0; i < 8; i++) {
      await _admit(buffer, new object());
    }

    var standard = _admit(buffer, new object()).AsTask();
    var interactive = _admit(buffer, new object(), interactive: true).AsTask();

    await Assert.That(standard.IsCompleted).IsFalse();
    await Assert.That(interactive.IsCompletedSuccessfully).IsTrue();
    await Assert.That(buffer.BufferedMessages).IsEqualTo(9);
  }

  [Test]
  public async Task Waiters_AreServedInArrivalOrderPerKind_AndALaterStandardAppendNeverPassesAnEarlierOneAsync() {
    var buffer = _buffer(_limits(messages: 10, bytes: 100, gateMax: 10, gateReserve: 1));
    var held = new object();
    await _admit(buffer, held, bytes: 80);

    var large = _admit(buffer, new object(), bytes: 50).AsTask();
    var small = _admit(buffer, new object(), bytes: 1).AsTask();
    var interactiveSmall = _admit(buffer, new object(), bytes: 5, interactive: true).AsTask();

    await Assert.That(small.IsCompleted).IsFalse().Because("a standard append queues behind the standard one waiting before it");
    await Assert.That(interactiveSmall.IsCompletedSuccessfully).IsTrue().Because("no interactive append waits, and it fits the full limit");
    buffer.ReleaseOne(held);
    await large;
    await small;
    await Assert.That(buffer.BufferedBytes).IsEqualTo(56);
  }

  [Test]
  public async Task AnInteractiveWaiter_PassesStandardWaitersThatDoNotFit_InArrivalOrderOfItsOwnKindAsync() {
    // 100 bytes, a fifth reserved: standard callers share 80.
    var buffer = _buffer(_limits(messages: 10, bytes: 100, gateMax: 10, gateReserve: 2));
    await _admit(buffer, new object(), bytes: 70);
    var small1 = new object();
    var small2 = new object();
    await _admit(buffer, small1, bytes: 5, interactive: true);
    await _admit(buffer, small2, bytes: 5, interactive: true);
    var standard = _admit(buffer, new object(), bytes: 20).AsTask();
    var firstInteractive = _admit(buffer, new object(), bytes: 26, interactive: true).AsTask();
    var secondInteractive = _admit(buffer, new object(), bytes: 1, interactive: true).AsTask();

    buffer.ReleaseOne(small1);

    await Assert.That(firstInteractive.IsCompleted).IsFalse().Because("75 + 26 is past the full limit");
    await Assert.That(secondInteractive.IsCompleted).IsFalse().Because("it fits, but queues behind the interactive one before it");
    buffer.ReleaseOne(small2);
    await firstInteractive;
    await secondInteractive;
    await Assert.That(standard.IsCompleted).IsFalse().Because("the standard share is 80 bytes and 97 are held");
  }

  [Test]
  public async Task ACallerHoldingATransactionOpen_GoesPastTheLimit_UpToTheCeiling_AndIsNamedAsync() {
    var logger = new FakeLogger<BatchBuffer>();
    using var meters = new TestMeterFactory();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(meters));
    using var helper = new MetricAssertionHelper(meters.CreatedMeters[0]);
    var buffer = _buffer(_limits(messages: 2, ceiling: 2), logger, metrics);
    var probes = 0;
    var inTransaction = new BatchAppendContext("Publishing Orders.OrderPlaced", () => { probes++; return true; });
    await _admit(buffer, new object(), context: inTransaction);
    await _admit(buffer, new object(), context: inTransaction);

    await Assert.That(probes).IsEqualTo(0).Because("the probe is asked only when an append would wait");
    await _admit(buffer, new object(), context: inTransaction);
    await _admit(buffer, new object(), context: inTransaction);
    var pastCeiling = _admit(buffer, new object(), context: inTransaction).AsTask();

    await Assert.That(pastCeiling.IsCompleted).IsFalse().Because("past twice the limit even a transaction waits");
    await Assert.That(buffer.BufferedMessages).IsEqualTo(4);
    await Assert.That(probes).IsEqualTo(3);
    await Assert.That(helper.GetByName("whizbang.batch_buffer.ceiling_overshoots").Sum(m => m.Value)).IsEqualTo(2);
    var warnings = logger.Collector.GetSnapshot().Where(r => r.Message.Contains("transaction open", StringComparison.Ordinal)).ToList();
    await Assert.That(warnings.Count).IsEqualTo(2);
    await Assert.That(warnings[0].Message).StartsWith("Publishing Orders.OrderPlaced holds a database transaction open");
  }

  [Test]
  public async Task ACallerWithoutATransaction_WaitsAtTheLimitAsync() {
    var buffer = _buffer(_limits(messages: 1));
    await _admit(buffer, new object());

    var notInTransaction = _admit(buffer, new object(), context: new BatchAppendContext("c", () => false)).AsTask();

    await Assert.That(notInTransaction.IsCompleted).IsFalse();
  }

  [Test]
  public async Task CancelingAWait_RemovesTheWaiter_AndLetsTheNextOneInAsync() {
    var buffer = _buffer(_limits(messages: 10, bytes: 100));
    var held = new object();
    await _admit(buffer, held, bytes: 90);
    using var cts = new CancellationTokenSource();
    var canceled = _admit(buffer, new object(), bytes: 50, cancellationToken: cts.Token).AsTask();
    var behind = _admit(buffer, new object(), bytes: 5).AsTask();

    await Assert.That(behind.IsCompleted).IsFalse();
    await cts.CancelAsync();

    await Assert.That(async () => await canceled).Throws<OperationCanceledException>();
    await behind;
    await Assert.That(buffer.WaitingAppends).IsEqualTo(0);
    buffer.ReleaseOne(held);
    await Assert.That(buffer.BufferedBytes).IsEqualTo(5).Because("the canceled append took no room");
  }

  [Test]
  public async Task AnAlreadyCanceledToken_IsRefusedWhenTheAppendWouldWaitAsync() {
    var buffer = _buffer(_limits(messages: 1));
    await _admit(buffer, new object());

    await Assert.That(async () => await _admit(buffer, new object(), cancellationToken: new CancellationToken(true)))
      .Throws<OperationCanceledException>();
    await Assert.That(buffer.WaitingAppends).IsEqualTo(0);
  }

  [Test]
  public async Task Stopping_CancelsEveryWaiter_AndRefusesAppendsThatWouldWaitAsync() {
    var buffer = _buffer(_limits(messages: 1));
    var held = new object();
    await _admit(buffer, held);
    var waiting = _admit(buffer, new object()).AsTask();

    buffer.Stop();

    await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
    await Assert.That(async () => await _admit(buffer, new object())).Throws<OperationCanceledException>();
    buffer.ReleaseOne(held);
    await Assert.That(_admit(buffer, new object()).AsTask().IsCompletedSuccessfully).IsTrue().Because("an append that fits is still admitted");
  }

  [Test]
  public async Task CancelingAWaitTheStopAlreadyEnded_ChangesNothingAsync() {
    var buffer = _buffer(_limits(messages: 1));
    await _admit(buffer, new object());
    using var cts = new CancellationTokenSource();
    var waiting = _admit(buffer, new object(), cancellationToken: cts.Token).AsTask();
    buffer.Stop();

    await cts.CancelAsync();

    var failure = await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
    await Assert.That(failure!.CancellationToken).IsNotEqualTo(cts.Token).Because("the stop ended the wait, not the token");
    await Assert.That(buffer.WaitingAppends).IsEqualTo(0);
  }

  [Test]
  public async Task ReleasingAMessageThatHoldsNoRoom_ChangesNothing_AndOneHeldTwiceIsReleasedTwiceAsync() {
    var buffer = _buffer(_limits(messages: 10));
    var twice = new object();
    await _admit(buffer, twice, bytes: 3);
    await _admit(buffer, twice, bytes: 3);

    buffer.ReleaseOne(new object());
    await Assert.That(buffer.BufferedMessages).IsEqualTo(2);
    buffer.ReleaseOne(twice);
    await Assert.That(buffer.BufferedMessages).IsEqualTo(1);
    buffer.ReleaseOne(twice);
    await Assert.That(buffer.BufferedMessages).IsEqualTo(0);
    await Assert.That(buffer.BufferedBytes).IsEqualTo(0);
    buffer.ReleaseOne(twice);
    await Assert.That(buffer.BufferedMessages).IsEqualTo(0);
  }

  [Test]
  public async Task WaitsStartingAndStopping_AreEachWarnedOnce_AndTheWaitsAreMeasuredAsync() {
    var logger = new FakeLogger<BatchBuffer>();
    var time = new FakeTimeProvider();
    using var meters = new TestMeterFactory();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(meters));
    using var helper = new MetricAssertionHelper(meters.CreatedMeters[0]);
    var buffer = _buffer(_limits(messages: 1), logger, metrics, time);
    var held = new object();
    await _admit(buffer, held, bytes: 7);
    var firstMessage = new object();
    var first = _admit(buffer, firstMessage).AsTask();
    var second = _admit(buffer, new object()).AsTask();

    time.Advance(TimeSpan.FromMilliseconds(250));
    buffer.ReleaseOne(held);
    await first;

    var warnings = logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Warning).Select(r => r.Message).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1);
    await Assert.That(warnings[0]).Contains("is full");
    await Assert.That(helper.GetByName("whizbang.batch_buffer.append_waits").Sum(m => m.Value)).IsEqualTo(2);
    await Assert.That(helper.GetByName("whizbang.batch_buffer.wait_duration").Single().Value).IsEqualTo(250);
    await Assert.That(helper.GetByName("whizbang.batch_buffer.messages")[^1].Value).IsEqualTo(1);
    await Assert.That(helper.GetByName("whizbang.batch_buffer.bytes")[^1].Value).IsEqualTo(1);

    buffer.ReleaseOne(firstMessage);
    await second;
    warnings = [.. logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Warning).Select(r => r.Message)];
    await Assert.That(warnings.Count).IsEqualTo(2);
    await Assert.That(warnings[1]).Contains("has room again after 2 append(s) waited");
  }

  [Test]
  public async Task TheStoreCallbacks_GiveTheBatchsRoomBack_OnlyOnceItIsStoredAsync() {
    var buffer = _buffer(_limits(messages: 10));
    var outbox = BoundedBatchStrategyTests.Outbox();
    var inbox = BoundedBatchStrategyTests.Inbox();
    await _admit(buffer, outbox);
    await _admit(buffer, inbox);
    var failOnce = true;
    var store = buffer.ReleasingOnStore((OutboxBulkFlushCallback)((_, _) =>
      failOnce ? Task.FromException(new TimeoutException("transient")) : Task.CompletedTask));

    await Assert.That(async () => await store([outbox], default)).Throws<TimeoutException>();
    await Assert.That(buffer.BufferedMessages).IsEqualTo(2).Because("a failed attempt is retried; the batch is still buffered");
    failOnce = false;
    await store([outbox], default);
    await buffer.ReleasingOnStore((InboxBulkFlushCallback)((_, _) => Task.CompletedTask))([inbox], default);

    await Assert.That(buffer.BufferedMessages).IsEqualTo(0);
  }

  [Test]
  public async Task TheFailureCallbacks_GiveTheBatchsRoomBack_EvenWhenRecordingFailsAsync() {
    var buffer = _buffer(_limits(messages: 10));
    var outbox = BoundedBatchStrategyTests.Outbox();
    var inbox = BoundedBatchStrategyTests.Inbox();
    await _admit(buffer, outbox);
    await _admit(buffer, inbox);
    var outboxFailed = buffer.ReleasingOnFailure((OutboxFlushFailureCallback)((_, _) => Task.FromResult(true)));
    var inboxFailed = buffer.ReleasingOnFailure((InboxFlushFailureCallback)((_, _) =>
      Task.FromException<bool>(new InvalidOperationException("the provider is gone"))));

    var deadLettered = await outboxFailed(new OutboxFlushFailure(Guid.Empty, [outbox], OutboxFlushFailureCause.NotRetryable, 1, null), default);
    await Assert.That(async () => await inboxFailed(
      new InboxFlushFailure(Guid.Empty, [inbox], InboxFlushFailureCause.NotRetryable, 1, null), default)).Throws<InvalidOperationException>();

    await Assert.That(deadLettered).IsTrue();
    await Assert.That(buffer.BufferedMessages).IsEqualTo(0);
  }

  [Test]
  public async Task TheCallbackWrappers_RefuseANullCallbackAsync() {
    var buffer = _buffer(_limits());
    await Assert.That(() => buffer.ReleasingOnStore((OutboxBulkFlushCallback)null!)).Throws<ArgumentNullException>();
    await Assert.That(() => buffer.ReleasingOnStore((InboxBulkFlushCallback)null!)).Throws<ArgumentNullException>();
    await Assert.That(() => buffer.ReleasingOnFailure((OutboxFlushFailureCallback)null!)).Throws<ArgumentNullException>();
    await Assert.That(() => buffer.ReleasingOnFailure((InboxFlushFailureCallback)null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AWaitThatEndedBeforeItsCancellationWasRegistered_DropsTheRegistrationAsync() {
    var waiter = new BatchBuffer.Waiter(new object(), 1, interactive: false, started: 0);
    var called = false;
    using var cts = new CancellationTokenSource();

    waiter.Detach();
    waiter.Attach(cts.Token.Register(() => called = true));
    await cts.CancelAsync();

    await Assert.That(called).IsFalse().Because("the registration was disposed as it arrived");
  }

  [Test]
  public async Task TheConstructor_RefusesMissingDependenciesAsync() {
    var limits = _limits();
    await Assert.That(() => new BatchBuffer(null!, new FakeLogger<BatchBuffer>(), null, TimeProvider.System)).Throws<ArgumentNullException>();
    await Assert.That(() => new BatchBuffer(limits, null!, null, TimeProvider.System)).Throws<ArgumentNullException>();
    await Assert.That(() => new BatchBuffer(limits, new FakeLogger<BatchBuffer>(), null, null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await _buffer(limits).AdmitAsync(null!, 1, false, _standard, default)).Throws<ArgumentNullException>();
    await Assert.That(() => _buffer(limits).Release(null!)).Throws<ArgumentNullException>();
  }
}
