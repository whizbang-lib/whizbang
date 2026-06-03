using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// v0.502 slice C.5 + C.6 — locks the default recovery policy matrix from the design doc.
///
/// <para>
/// These tests are the formal contract between the design (plans/dlq-recovery.md § "Error-
/// class policy") and the running code. Changing any default here must trace back to a doc
/// update first.
/// </para>
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
public class DeadLetterRecoveryPolicyTests {

  private static DefaultDeadLetterRecoveryPolicy _newPolicy(DeadLetterRecoveryOptions? options = null) {
    options ??= new DeadLetterRecoveryOptions();
    return new DefaultDeadLetterRecoveryPolicy(Options.Create(options));
  }

  private static DeadLetterEntry _entry(
      MessageFailureReason reason,
      Guid? streamId = null,
      DeadLetterRecoveryStatus status = DeadLetterRecoveryStatus.Pending) {
    return new DeadLetterEntry(
      DeadLetterId: Guid.NewGuid(),
      SourceTable: DeadLetterSourceTable.INBOX,
      SourceId: Guid.NewGuid(),
      StreamId: streamId,
      MessageType: "Test.Event",
      FailureReason: reason,
      AttemptsWhenDlq: 10,
      DeadLetteredAt: DateTimeOffset.UtcNow,
      RecoveryStatus: status,
      RecoveryAttempts: 0,
      Generation: "test/0.0.1");
  }

  [Test]
  public async Task ThrottledReason_DefaultsToAggressiveRetryAsync() {
    var policy = _newPolicy().GetPolicy(_entry(MessageFailureReason.Throttled));
    await Assert.That(policy.Name).IsEqualTo("AggressiveRetry");
    await Assert.That(policy.MaxRecoveryAttempts).IsEqualTo(3);
    await Assert.That(policy.Cooldown).IsEqualTo(TimeSpan.FromMinutes(30));
    await Assert.That(policy.HoldForReviewAfterExhaustion).IsFalse()
      .Because("throttling is transient; let it permanently-fail rather than hold for human");
  }

  [Test]
  public async Task ValidationErrorReason_DefaultsToHoldForReviewAsync() {
    var policy = _newPolicy().GetPolicy(_entry(MessageFailureReason.ValidationError));
    await Assert.That(policy.Name).IsEqualTo("HoldForReview");
    await Assert.That(policy.MaxRecoveryAttempts).IsEqualTo(0)
      .Because("validation errors almost always need a code fix — auto-retry is wasted work");
    await Assert.That(policy.HoldForReviewAfterExhaustion).IsTrue();
  }

  [Test]
  public async Task SerializationErrorReason_DefaultsToHoldForReviewAsync() {
    var policy = _newPolicy().GetPolicy(_entry(MessageFailureReason.SerializationError));
    await Assert.That(policy.Name).IsEqualTo("HoldForReview");
    await Assert.That(policy.MaxRecoveryAttempts).IsEqualTo(0);
  }

  [Test]
  public async Task EventStorageFailureReason_DefaultsToHoldForReviewAsync() {
    var policy = _newPolicy().GetPolicy(_entry(MessageFailureReason.EventStorageFailure));
    await Assert.That(policy.Name).IsEqualTo("HoldForReview");
    await Assert.That(policy.HoldForReviewAfterExhaustion).IsTrue();
  }

  [Test]
  public async Task LeaseExpiredReason_DefaultsToAggressiveImmediateRetryAsync() {
    var policy = _newPolicy().GetPolicy(_entry(MessageFailureReason.LeaseExpired));
    await Assert.That(policy.MaxRecoveryAttempts).IsEqualTo(5);
    await Assert.That(policy.Cooldown).IsEqualTo(TimeSpan.Zero)
      .Because("lease expiry is usually a transient pod-restart artifact; retry immediately");
  }

  [Test]
  public async Task UnknownReason_FallsBackToOneShotThenHoldAsync() {
    // Custom reasons (or unrecognized future ones) get the safest default: try once, then
    // hold for review. Avoids both infinite retry AND immediate permanent-fail on rows
    // whose actual handling is unclear.
    var policy = _newPolicy().GetPolicy(_entry(MessageFailureReason.Unknown));
    await Assert.That(policy.Name).IsEqualTo("OneShotThenHold");
    await Assert.That(policy.MaxRecoveryAttempts).IsEqualTo(1);
    await Assert.That(policy.HoldForReviewAfterExhaustion).IsTrue();
  }

  [Test]
  public async Task GetStreamMode_StreamIdSet_TailAwareAsync() {
    var mode = _newPolicy().GetStreamMode(_entry(MessageFailureReason.Throttled, streamId: Guid.NewGuid()));
    await Assert.That(mode).IsEqualTo(StreamRecoveryMode.TailAware)
      .Because("default for stream-bound messages: coordinate recovery with sibling DLQ entries");
  }

  [Test]
  public async Task GetStreamMode_NoStreamId_PerMessageAsync() {
    var mode = _newPolicy().GetStreamMode(_entry(MessageFailureReason.Throttled, streamId: null));
    await Assert.That(mode).IsEqualTo(StreamRecoveryMode.PerMessage);
  }

  [Test]
  public async Task ShouldRecover_PendingEntry_TrueAsync() {
    var should = _newPolicy().ShouldRecover(_entry(MessageFailureReason.Throttled, status: DeadLetterRecoveryStatus.Pending));
    await Assert.That(should).IsTrue();
  }

  [Test]
  public async Task ShouldRecover_HoldForReview_FalseAsync() {
    var should = _newPolicy().ShouldRecover(_entry(MessageFailureReason.ValidationError, status: DeadLetterRecoveryStatus.HoldForReview));
    await Assert.That(should).IsFalse();
  }

  [Test]
  public async Task ShouldRecover_AlreadyRecovered_FalseAsync() {
    var should = _newPolicy().ShouldRecover(_entry(MessageFailureReason.Throttled, status: DeadLetterRecoveryStatus.Recovered));
    await Assert.That(should).IsFalse()
      .Because("recovered rows must never re-enter the recovery loop");
  }

  [Test]
  public async Task ShouldRecover_PermanentlyFailed_FalseAsync() {
    var should = _newPolicy().ShouldRecover(_entry(MessageFailureReason.Throttled, status: DeadLetterRecoveryStatus.PermanentlyFailed));
    await Assert.That(should).IsFalse();
  }

  [Test]
  public async Task RecoveryStatusEnum_HasExpectedValuesAsync() {
    // Enum values are persisted in wh_dead_letters.recovery_status — locking them prevents
    // a future enum reorder from silently misinterpreting historical rows.
#pragma warning disable TUnitAssertions0005
    await Assert.That((int)DeadLetterRecoveryStatus.Pending).IsEqualTo(0);
    await Assert.That((int)DeadLetterRecoveryStatus.Recovering).IsEqualTo(1);
    await Assert.That((int)DeadLetterRecoveryStatus.HoldForReview).IsEqualTo(2);
    await Assert.That((int)DeadLetterRecoveryStatus.Recovered).IsEqualTo(3);
    await Assert.That((int)DeadLetterRecoveryStatus.PermanentlyFailed).IsEqualTo(4);
#pragma warning restore TUnitAssertions0005
  }

  [Test]
  public async Task DispositionEnum_HasExpectedValuesAsync() {
#pragma warning disable TUnitAssertions0005
    await Assert.That((int)DeadLetterDisposition.None).IsEqualTo(0);
    await Assert.That((int)DeadLetterDisposition.RetryNow).IsEqualTo(1);
    await Assert.That((int)DeadLetterDisposition.HoldIndefinitely).IsEqualTo(2);
    await Assert.That((int)DeadLetterDisposition.MarkPermanentlyFailed).IsEqualTo(3);
#pragma warning restore TUnitAssertions0005
  }
}
