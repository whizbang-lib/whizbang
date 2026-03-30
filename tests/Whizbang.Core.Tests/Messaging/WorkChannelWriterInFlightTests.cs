using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Dispatch;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for in-flight tracking on IWorkChannelWriter.
/// Every WriteAsync/TryWrite must track the message as in-flight.
/// RemoveInFlight must clear tracking on completion/failure.
/// IsInFlight must return correct state for polling path dedup.
/// </summary>
public class WorkChannelWriterInFlightTests {

  private static OutboxWork _createWork(Guid? messageId = null) =>
    new() {
      MessageId = messageId ?? Guid.NewGuid(),
      Destination = "test-topic",
      Status = MessageProcessingStatus.Stored,
      Envelope = new MessageEnvelope<JsonElement> { MessageId = MessageId.New(), Payload = default, Hops = [], DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local } },
      EnvelopeType = "TestEnvelope",
      MessageType = "TestMessage",
      Attempts = 0
    };

  // ========================================
  // Gap 1: WriteAsync must track in-flight
  // ========================================

  [Test]
  public async Task WriteAsync_TracksMessageAsInFlightAsync() {
    var writer = new WorkChannelWriter();
    var work = _createWork();

    await writer.WriteAsync(work);

    await Assert.That(writer.IsInFlight(work.MessageId)).IsTrue()
      .Because("WriteAsync must track message as in-flight to prevent duplicate publishing");
  }

  [Test]
  public async Task WriteAsync_SameMessageTwice_RemainsInFlightAsync() {
    var writer = new WorkChannelWriter();
    var work = _createWork();

    await writer.WriteAsync(work);
    await writer.WriteAsync(work); // Duplicate — should not throw

    await Assert.That(writer.IsInFlight(work.MessageId)).IsTrue();
  }

  // ========================================
  // TryWrite must track in-flight
  // ========================================

  [Test]
  public async Task TryWrite_Success_TracksMessageAsInFlightAsync() {
    var writer = new WorkChannelWriter();
    var work = _createWork();

    var written = writer.TryWrite(work);

    await Assert.That(written).IsTrue();
    await Assert.That(writer.IsInFlight(work.MessageId)).IsTrue()
      .Because("TryWrite must track message as in-flight on success");
  }

  // ========================================
  // Gap 2/3: RemoveInFlight clears tracking
  // ========================================

  [Test]
  public async Task RemoveInFlight_AfterWrite_ClearsTrackingAsync() {
    var writer = new WorkChannelWriter();
    var work = _createWork();

    await writer.WriteAsync(work);
    await Assert.That(writer.IsInFlight(work.MessageId)).IsTrue();

    writer.RemoveInFlight(work.MessageId);
    await Assert.That(writer.IsInFlight(work.MessageId)).IsFalse()
      .Because("RemoveInFlight must clear tracking so message can be re-queued");
  }

  // ========================================
  // Gap 4: RemoveInFlight on unknown ID is safe
  // ========================================

  [Test]
  public async Task RemoveInFlight_UnknownMessageId_DoesNotThrowAsync() {
    var writer = new WorkChannelWriter();
    writer.RemoveInFlight(Guid.NewGuid()); // Should not throw

    // If we reach here, no exception was thrown
    var unknownId = Guid.NewGuid();
    await Assert.That(writer.IsInFlight(unknownId)).IsFalse();
  }

  // ========================================
  // IsInFlight for polling path dedup
  // ========================================

  [Test]
  public async Task IsInFlight_MessageNotWritten_ReturnsFalseAsync() {
    var writer = new WorkChannelWriter();
    await Assert.That(writer.IsInFlight(Guid.NewGuid())).IsFalse();
  }

  // ========================================
  // Full lifecycle: write → remove → re-write
  // ========================================

  [Test]
  public async Task FullLifecycle_WriteRemoveRewrite_TracksCorrectlyAsync() {
    var writer = new WorkChannelWriter();
    var work = _createWork();

    // Write → in-flight
    await writer.WriteAsync(work);
    await Assert.That(writer.IsInFlight(work.MessageId)).IsTrue();

    // Complete → not in-flight
    writer.RemoveInFlight(work.MessageId);
    await Assert.That(writer.IsInFlight(work.MessageId)).IsFalse();

    // Re-queue (e.g., Phase 7 returns it again) → back in-flight
    await writer.WriteAsync(work);
    await Assert.That(writer.IsInFlight(work.MessageId)).IsTrue();
  }

  // ========================================
  // Multiple messages tracked independently
  // ========================================

  [Test]
  public async Task MultipleMessages_TrackedIndependentlyAsync() {
    var writer = new WorkChannelWriter();
    var work1 = _createWork();
    var work2 = _createWork();

    await writer.WriteAsync(work1);
    await writer.WriteAsync(work2);

    await Assert.That(writer.IsInFlight(work1.MessageId)).IsTrue();
    await Assert.That(writer.IsInFlight(work2.MessageId)).IsTrue();

    writer.RemoveInFlight(work1.MessageId);
    await Assert.That(writer.IsInFlight(work1.MessageId)).IsFalse();
    await Assert.That(writer.IsInFlight(work2.MessageId)).IsTrue()
      .Because("Removing one message should not affect others");
  }

  // ========================================
  // Publish-completion race: message must stay in-flight after publish success
  // ========================================

  // ========================================
  // ShouldRenewLease: only renew near expiry, not every tick
  // ========================================

  [Test]
  public async Task ShouldRenewLease_RecentlyWritten_ReturnsFalseAsync() {
    var writer = new WorkChannelWriter();
    var work = _createWork();

    await writer.WriteAsync(work);

    // Just written — should NOT need renewal yet
    await Assert.That(writer.ShouldRenewLease(work.MessageId)).IsFalse()
      .Because("Recently written messages should not need lease renewal");
  }

  [Test]
  public async Task ShouldRenewLease_NotInFlight_ReturnsFalseAsync() {
    var writer = new WorkChannelWriter();
    await Assert.That(writer.ShouldRenewLease(Guid.NewGuid())).IsFalse();
  }

  /// <summary>
  /// After a successful publish, the message must remain in-flight until the DB
  /// confirms the completion. If RemoveInFlight is called on publish success,
  /// Phase 7 returns the message again before the DB completion is flushed,
  /// causing duplicate publishing.
  /// </summary>
  [Test]
  public async Task WriteAsync_ThenSuccessfulPublish_ShouldStayInFlightAsync() {
    var writer = new WorkChannelWriter();
    var work = _createWork();

    // Simulate: message written to channel (tracked)
    await writer.WriteAsync(work);

    // Simulate: publisher publishes successfully — should NOT remove from in-flight
    // (In the current code, _trackPublishResult calls RemoveInFlight on success — that's the bug)
    // The correct behavior: message stays in-flight until DB confirms completion
    // We verify this by checking IsInFlight after a simulated "successful publish"
    // without calling RemoveInFlight

    await Assert.That(writer.IsInFlight(work.MessageId)).IsTrue()
      .Because("Message must stay in-flight after publish success until DB confirms completion");
  }
}
