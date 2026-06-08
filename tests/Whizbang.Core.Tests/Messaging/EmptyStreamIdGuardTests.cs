using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Locks the v0.657 slice 2 invariants around <see cref="EmptyStreamIdGuard"/> — the
/// storage-time check that intercepts producer-side <c>Guid.Empty</c> stream_id
/// rows BEFORE they hit <c>wh_outbox</c> / <c>wh_inbox</c>.
/// </summary>
/// <remarks>
/// Slot-3 forensic (Jun 2026): a JDX producer wrote a <c>RemoveShellUserCommand</c>
/// with <c>stream_id = Guid.Empty</c> and the row sat silently stuck for 24 h.
/// With <see cref="EmptyStreamIdPolicy.Reject"/> (the v0.657 default), this guard
/// throws <see cref="EmptyStreamIdException"/> at <c>StoreOutboxMessagesAsync</c>
/// time so the producer's commit fails loud — the bug surfaces at the producer,
/// not 992 attempts later at the silent consumer.
/// </remarks>
/// <docs>operations/configuration/empty-stream-id-policy</docs>
public class EmptyStreamIdGuardTests {

  /// <summary>
  /// The slot-3 case: Reject policy + Empty stream_id → typed exception. The
  /// exception carries message_id + message_type so the producer's stack trace
  /// identifies the exact bad row.
  /// </summary>
  [Test]
  public async Task ThrowIfEmpty_EmptyStreamIdUnderReject_ThrowsAsync() {
    Guid messageId = TrackedGuid.NewMedo();
    var messageType = "JDX.Contracts.Auth.RemoveShellUserCommand, JDX.Contracts";

    var ex = await Assert.That(() => {
      EmptyStreamIdGuard.ThrowIfEmpty(messageId, messageType, Guid.Empty, EmptyStreamIdPolicy.Reject);
    }).ThrowsExactly<EmptyStreamIdException>();

    await Assert.That(ex!.MessageId).IsEqualTo(messageId)
      .Because("Producer debugging via stack trace needs the exact message_id of the offending row to look it up in their write-side context.");
    await Assert.That(ex.MessageType).IsEqualTo(messageType)
      .Because("Naming the contract type tells the producer which call site to fix.");
  }

  /// <summary>
  /// Under FallbackToMessageId / DeadLetter / Purge, the storage guard MUST NOT
  /// throw — the row lands and the coordinator-side recovery (slice 3) handles it.
  /// </summary>
  [Test]
  [Arguments(EmptyStreamIdPolicy.FallbackToMessageId)]
  [Arguments(EmptyStreamIdPolicy.DeadLetter)]
  [Arguments(EmptyStreamIdPolicy.Purge)]
  public async Task ThrowIfEmpty_NonRejectPolicies_DoesNotThrowAsync(EmptyStreamIdPolicy policy) {
    await Assert.That(() => {
      EmptyStreamIdGuard.ThrowIfEmpty(Guid.NewGuid(), "Sample.Type", Guid.Empty, policy);
    }).ThrowsNothing()
      .Because("Storage-time Reject is the only policy that fails at write time; the other policies allow the row to land so coordinator/drainer behavior can apply per the policy intent.");
  }

  /// <summary>
  /// Real stream IDs ride through unchanged, even under Reject.
  /// </summary>
  [Test]
  public async Task ThrowIfEmpty_RealStreamIdUnderReject_DoesNotThrowAsync() {
    await Assert.That(() => {
      EmptyStreamIdGuard.ThrowIfEmpty(Guid.NewGuid(), "Sample.Type", TrackedGuid.NewMedo(), EmptyStreamIdPolicy.Reject);
    }).ThrowsNothing()
      .Because("The guard only fires on Guid.Empty — real UUIDs pass through.");
  }

  /// <summary>
  /// NULL stream_id is the legitimate singleton-stream marker — must pass under
  /// any policy. Distinguishing NULL from Empty is the whole point of this slice.
  /// </summary>
  [Test]
  public async Task ThrowIfEmpty_NullStreamIdUnderReject_DoesNotThrowAsync() {
    await Assert.That(() => {
      EmptyStreamIdGuard.ThrowIfEmpty(Guid.NewGuid(), "Sample.Type", streamId: null, EmptyStreamIdPolicy.Reject);
    }).ThrowsNothing()
      .Because("NULL stream_id is the documented marker for event-store-only / singleton-stream messages. The slot-3 bug was Empty masquerading as 'valid stream' through the `??` coalesce — NULL never had that problem.");
  }
}
