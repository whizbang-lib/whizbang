using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// v0.502 — regression locks for <see cref="DefaultDeadLetterRecoveryPolicy"/>. The default
/// policy is the matrix operators inherit when they don't register a custom
/// <see cref="IDeadLetterRecoveryPolicy"/>. The shipped defaults in
/// <see cref="DeadLetterRecoveryOptions.PolicyByReason"/> drive the recovery worker's
/// per-reason retry cadence and the HoldForReview-after-exhaustion semantic; any silent
/// shift in this matrix would break operator expectations downstream.
/// </summary>
public class DefaultDeadLetterRecoveryPolicyTests {

  private static DefaultDeadLetterRecoveryPolicy _newPolicy(DeadLetterRecoveryOptions? opts = null) {
    return new DefaultDeadLetterRecoveryPolicy(Options.Create(opts ?? new DeadLetterRecoveryOptions()));
  }

  private static DeadLetterEntry _entry(
      MessageFailureReason reason,
      DeadLetterRecoveryStatus status = DeadLetterRecoveryStatus.Pending,
      Guid? streamId = null) {
    return new DeadLetterEntry(
      DeadLetterId: (Guid)TrackedGuid.NewMedo(),
      SourceTable: DeadLetterSourceTable.INBOX,
      SourceId: (Guid)TrackedGuid.NewMedo(),
      StreamId: streamId,
      MessageType: "Test.Event",
      FailureReason: reason,
      AttemptsWhenDlq: 10,
      DeadLetteredAt: DateTimeOffset.UtcNow,
      RecoveryStatus: status,
      RecoveryAttempts: 0,
      Generation: "whizbang/test-1");
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    // ArgumentNullException.ThrowIfNull is unwrapped through Options.Create when the inner
    // value would be null; the policy itself checks options?.Value. Verify the contract.
    await Assert.That(() => new DefaultDeadLetterRecoveryPolicy(null!)).Throws<ArgumentNullException>();
  }

  // ===== GetPolicy =====

  [Test]
  public async Task GetPolicy_KnownReason_ReturnsConfiguredPolicyAsync() {
    var policy = _newPolicy();
    var result = policy.GetPolicy(_entry(MessageFailureReason.Throttled));
    // Throttled default ships as AggressiveRetry / MaxRecoveryAttempts=3 / Cooldown=30 min.
    await Assert.That(result.Name).IsEqualTo("AggressiveRetry");
    await Assert.That(result.MaxRecoveryAttempts).IsEqualTo(3);
    await Assert.That(result.Cooldown).IsEqualTo(TimeSpan.FromMinutes(30));
    await Assert.That(result.HoldForReviewAfterExhaustion).IsFalse();
  }

  [Test]
  public async Task GetPolicy_TransportException_ReturnsMediumRetryAsync() {
    var policy = _newPolicy();
    var result = policy.GetPolicy(_entry(MessageFailureReason.TransportException));
    await Assert.That(result.Name).IsEqualTo("MediumRetry");
    await Assert.That(result.MaxRecoveryAttempts).IsEqualTo(3);
    await Assert.That(result.Cooldown).IsEqualTo(TimeSpan.FromHours(1));
  }

  [Test]
  public async Task GetPolicy_MaxAttemptsExceeded_HoldsAfterExhaustionAsync() {
    var policy = _newPolicy();
    var result = policy.GetPolicy(_entry(MessageFailureReason.MaxAttemptsExceeded));
    await Assert.That(result.Name).IsEqualTo("ConservativeRetry");
    await Assert.That(result.MaxRecoveryAttempts).IsEqualTo(1);
    await Assert.That(result.Cooldown).IsEqualTo(TimeSpan.FromHours(6));
    await Assert.That(result.HoldForReviewAfterExhaustion).IsTrue();
  }

  [Test]
  public async Task GetPolicy_ValidationError_HoldsForReviewWithoutRetryAsync() {
    var policy = _newPolicy();
    var result = policy.GetPolicy(_entry(MessageFailureReason.ValidationError));
    await Assert.That(result.Name).IsEqualTo("HoldForReview");
    await Assert.That(result.MaxRecoveryAttempts).IsEqualTo(0);
    await Assert.That(result.HoldForReviewAfterExhaustion).IsTrue();
  }

  [Test]
  public async Task GetPolicy_UnknownReasonNotInDictionary_FallsBackToUnknownEntryAsync() {
    // The Unknown entry exists in defaults and serves as the fallback. Build options with
    // ONLY the Unknown entry to verify the fallback path explicitly.
    var opts = new DeadLetterRecoveryOptions {
      PolicyByReason = new Dictionary<MessageFailureReason, RecoveryPolicy> {
        [MessageFailureReason.Unknown] = new("FallbackTest", 99, TimeSpan.FromMinutes(7), HoldForReviewAfterExhaustion: true),
      },
    };
    var policy = _newPolicy(opts);
    // Pass a reason NOT in the dictionary; should hit the fallback path.
    var result = policy.GetPolicy(_entry(MessageFailureReason.Throttled));
    await Assert.That(result.Name).IsEqualTo("FallbackTest");
    await Assert.That(result.MaxRecoveryAttempts).IsEqualTo(99);
    await Assert.That(result.Cooldown).IsEqualTo(TimeSpan.FromMinutes(7));
  }

  // ===== GetStreamMode =====

  [Test]
  public async Task GetStreamMode_StreamIdPresent_ReturnsTailAwareAsync() {
    var policy = _newPolicy();
    var mode = policy.GetStreamMode(_entry(MessageFailureReason.Throttled, streamId: (Guid)TrackedGuid.NewMedo()));
    await Assert.That(mode).IsEqualTo(StreamRecoveryMode.TailAware);
  }

  [Test]
  public async Task GetStreamMode_StreamIdNull_ReturnsPerMessageAsync() {
    var policy = _newPolicy();
    var mode = policy.GetStreamMode(_entry(MessageFailureReason.Throttled, streamId: null));
    await Assert.That(mode).IsEqualTo(StreamRecoveryMode.PerMessage);
  }

  // ===== ShouldRecover =====

  [Test]
  public async Task ShouldRecover_PendingStatus_ReturnsTrueAsync() {
    var policy = _newPolicy();
    var should = policy.ShouldRecover(_entry(MessageFailureReason.Throttled, status: DeadLetterRecoveryStatus.Pending));
    await Assert.That(should).IsTrue();
  }

  [Test]
  public async Task ShouldRecover_RecoveringStatus_ReturnsTrueAsync() {
    // Recovering is a transient state — the worker may re-attempt these rows.
    var policy = _newPolicy();
    var should = policy.ShouldRecover(_entry(MessageFailureReason.Throttled, status: DeadLetterRecoveryStatus.Recovering));
    await Assert.That(should).IsTrue();
  }

  [Test]
  public async Task ShouldRecover_HoldForReview_ReturnsFalseAsync() {
    var policy = _newPolicy();
    var should = policy.ShouldRecover(_entry(MessageFailureReason.Throttled, status: DeadLetterRecoveryStatus.HoldForReview));
    await Assert.That(should).IsFalse();
  }

  [Test]
  public async Task ShouldRecover_Recovered_ReturnsFalseAsync() {
    var policy = _newPolicy();
    var should = policy.ShouldRecover(_entry(MessageFailureReason.Throttled, status: DeadLetterRecoveryStatus.Recovered));
    await Assert.That(should).IsFalse();
  }

  [Test]
  public async Task ShouldRecover_PermanentlyFailed_ReturnsFalseAsync() {
    var policy = _newPolicy();
    var should = policy.ShouldRecover(_entry(MessageFailureReason.Throttled, status: DeadLetterRecoveryStatus.PermanentlyFailed));
    await Assert.That(should).IsFalse();
  }

  [Test]
  public async Task GetPolicy_BrokerDeadLetter_ReturnsMediumRetryAsync() {
    var policy = _newPolicy();

    var result = policy.GetPolicy(_entry(MessageFailureReason.BrokerDeadLetter));

    await Assert.That(result.Name).IsEqualTo("MediumRetry")
      .Because("a broker dead-letter usually means 'this build could not process the message' — "
             + "worth retrying on a sane cadence (and generation replay re-offers it after each "
             + "deploy), not aggressively and not parked-on-arrival");
    await Assert.That(result.MaxRecoveryAttempts).IsEqualTo(3);
  }
}

