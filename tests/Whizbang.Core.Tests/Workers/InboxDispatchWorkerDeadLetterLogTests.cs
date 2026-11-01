// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1901: a message the inbox dead-letters gets one terminal record, logged at Error by default with its message
/// type and its last recorded error, so a consumer that alerts on Error hears about a message whose effect will never
/// happen. A failure that will be retried stays at Warning, and a row that never reaches the dead-letter store (a
/// control-plane drop, or a row another path already moved) gets no record.
/// </summary>
/// <docs>operations/observability/logging-categories#dead-lettered-messages</docs>
[Category("Workers")]
public sealed class InboxDispatchWorkerDeadLetterLogTests {
  private const int DEAD_LETTER_EVENT_ID = 1901;
  private const string DEAD_LETTER_EVENT_NAME = "MessageDeadLettered";
  private const string DOMAIN_TYPE = "Contracts.Orders.OrderPlaced, Contracts";

  private static IReadOnlyList<(LogLevel Level, int EventId, string Message, Exception? Exception, string? EventName)> _records(InboxDispatchUnitHarness h) =>
    [.. h.Logs.Where(l => l.EventId == DEAD_LETTER_EVENT_ID)];

  [Test]
  public async Task MaxAttempts_MovedToTheDeadLetterStore_LogsOneErrorRecordWithTypeAndLastErrorAsync() {
    var h = new InboxDispatchUnitHarness();
    var work = InboxDispatchUnitHarness.Work(attempts: 11, error: "System.TimeoutException: the payment gateway timed out",
      messageType: DOMAIN_TYPE);

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var record = _records(h).Single();
    await Assert.That(record.Level).IsEqualTo(LogLevel.Error)
      .Because("#1901: a dead-lettered message will never succeed on its own, so it is logged at Error by default");
    await Assert.That(record.EventName).IsEqualTo(DEAD_LETTER_EVENT_NAME)
      .Because("every dead-letter path logs the same event, so one query finds them all");
    await Assert.That(record.Message).Contains(work.MessageId.ToString())
      .And.Contains(DOMAIN_TYPE)
      .And.Contains(nameof(MessageFailureReason.MaxAttemptsExceeded))
      .And.Contains("the payment gateway timed out")
      .Because("the one terminal record says what failed and why");
    await Assert.That(h.Logs.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("dead-lettered", StringComparison.Ordinal))).IsFalse()
      .Because("the record replaces the Warning the inbox used to log for a dead letter, so the message is not reported twice");
  }

  [Test]
  public async Task MaxAttempts_NoRecordedError_RecordNamesTheExhaustedAttemptsAsync() {
    var h = new InboxDispatchUnitHarness();
    var work = InboxDispatchUnitHarness.Work(attempts: 11, error: null, messageType: DOMAIN_TYPE);

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(_records(h).Single().Message).Contains("attempts=11 > max=10")
      .Because("with no recorded error the record still says why the message stopped: its attempts ran out");
  }

  [Test]
  public async Task MaxAttempts_NoDeadLetterStore_LogsTheRecordForTheTerminalCompletionAsync() {
    var h = new InboxDispatchUnitHarness(deadLetters: NullDeadLetterStore.Instance);
    var work = InboxDispatchUnitHarness.Work(attempts: 11, error: "boom", messageType: DOMAIN_TYPE);

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(_records(h).Single().Level).IsEqualTo(LogLevel.Error)
      .Because("without a store the row is still finished for good, so the message is just as lost and still needs the record");
    await Assert.That(h.Commits.All).Count().IsEqualTo(1);
  }

  [Test]
  public async Task MaxAttempts_DeadLetterMoveThrows_LogsTheRecordForTheTerminalCompletionAsync() {
    var h = new InboxDispatchUnitHarness(deadLetters: new ThrowingDeadLetterStore());
    var work = InboxDispatchUnitHarness.Work(attempts: 11, error: "boom", messageType: DOMAIN_TYPE);

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(_records(h).Single().Level).IsEqualTo(LogLevel.Error)
      .Because("a move that fails falls back to a terminal completion, which ends the message just the same");
  }

  [Test]
  public async Task MaxAttempts_RowAlreadyGone_LogsNoRecordAsync() {
    var h = new InboxDispatchUnitHarness(deadLetters: new AlreadyGoneDeadLetterStore());
    var work = InboxDispatchUnitHarness.Work(attempts: 11, error: "boom", messageType: DOMAIN_TYPE);

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(_records(h)).IsEmpty()
      .Because("the row was already moved by another path, which logged its own record; a second would report one message twice");
  }

  [Test]
  public async Task MaxAttempts_ControlPlaneDrop_LogsTheDropAtWarningAndNoRecordAsync() {
    var h = new InboxDispatchUnitHarness();
    var work = InboxDispatchUnitHarness.Work(attempts: 11, error: "boom",
      messageType: TypeNameFormatter.Format(typeof(IntegrityCheckpoint)));

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(_records(h)).IsEmpty()
      .Because("control-plane traffic is dropped and re-issued on its own cadence, never dead-lettered, so it raises no dead-letter alert");
    await Assert.That(h.Logs.Single(l => l.EventId == 11).Level).IsEqualTo(LogLevel.Warning);
  }

  [Test]
  public async Task Dispatch_ThatThrowsBelowTheBound_LogsWarningAndNoRecordAsync() {
    var h = new InboxDispatchUnitHarness();
    h.Commits.Throw = new InvalidOperationException("commit channel closed");
    var work = InboxDispatchUnitHarness.Work(attempts: 1, messageType: DOMAIN_TYPE);

    await h.Worker.DispatchQueuedAsync(work, CancellationToken.None);

    var attempt = h.Logs.Single(l => l.EventId == 4);
    await Assert.That(attempt.Level).IsEqualTo(LogLevel.Warning)
      .Because("#1901: a failed attempt may still succeed on retry, so it stays at Warning");
    await Assert.That(attempt.Exception).IsTypeOf<InvalidOperationException>();
    await Assert.That(_records(h)).IsEmpty()
      .Because("an attempt that will be retried is not a dead letter");
  }

  private sealed class ThrowingDeadLetterStore : IDeadLetterStore {
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default)
      => throw new InvalidOperationException("dead-letter table unavailable");
  }

  private sealed class AlreadyGoneDeadLetterStore : IDeadLetterStore {
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default)
      => Task.FromResult<Guid?>(null);
  }
}
