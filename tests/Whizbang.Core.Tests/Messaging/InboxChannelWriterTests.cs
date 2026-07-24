using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Direct tests for <see cref="InboxChannelWriter"/> — the unbounded
/// channel + in-flight tracker used by <c>WorkChannelWriter</c> to feed
/// the inbox dispatch worker.
///
/// Locked invariants:
///   1. WriteAsync / TryWrite both register the message_id in the
///      in-flight set BEFORE pushing onto the channel (no race window).
///   2. ShouldRenewLease honors the 150 s threshold (half of the default
///      300 s lease).
///   3. RemoveInFlight is a TryRemove no-op on unknown ids.
///   4. Complete signals the channel reader.
///   5. SignalNewInboxWorkAvailable fires the event when subscribers exist;
///      no-ops cleanly when there are none.
/// </summary>
/// <docs>messaging/inbox-channel</docs>
public class InboxChannelWriterTests {

  [Test]
  public async Task WriteAsync_RegistersInFlight_AndPushesToReaderAsync() {
    var writer = new InboxChannelWriter();
    var work = _createWork();

    await writer.WriteAsync(work);

    await Assert.That(writer.IsInFlight(work.MessageId)).IsTrue();
    var read = await writer.Reader.ReadAsync();
    await Assert.That(read.MessageId).IsEqualTo(work.MessageId);
  }

  [Test]
  public async Task TryWrite_AcceptsAndRegistersInFlightAsync() {
    var writer = new InboxChannelWriter();
    var work = _createWork();

    var accepted = writer.TryWrite(work);

    await Assert.That(accepted).IsTrue();
    await Assert.That(writer.IsInFlight(work.MessageId)).IsTrue();
  }

  [Test]
  public async Task IsInFlight_UnknownId_ReturnsFalseAsync() {
    var writer = new InboxChannelWriter();

    await Assert.That(writer.IsInFlight(Guid.NewGuid())).IsFalse();
  }

  [Test]
  public async Task RemoveInFlight_ClearsRegisteredIdAsync() {
    var writer = new InboxChannelWriter();
    var work = _createWork();
    writer.TryWrite(work);
    await Assert.That(writer.IsInFlight(work.MessageId)).IsTrue();

    writer.RemoveInFlight(work.MessageId);

    await Assert.That(writer.IsInFlight(work.MessageId)).IsFalse();
  }

  [Test]
  public async Task RemoveInFlight_UnknownId_IsSafeNoOpAsync() {
    var writer = new InboxChannelWriter();

    // TryRemove pattern — no exception, no state change.
    writer.RemoveInFlight(Guid.NewGuid());

    await Assert.That(writer.IsInFlight(Guid.NewGuid())).IsFalse();
  }

  [Test]
  public async Task ShouldRenewLease_UnknownId_ReturnsFalseAsync() {
    var writer = new InboxChannelWriter();

    await Assert.That(writer.ShouldRenewLease(Guid.NewGuid())).IsFalse();
  }

  [Test]
  public async Task ShouldRenewLease_FreshlyTracked_ReturnsFalseAsync() {
    var writer = new InboxChannelWriter();
    var work = _createWork();
    writer.TryWrite(work);

    // Just tracked — well under the 150 s renewal threshold.
    await Assert.That(writer.ShouldRenewLease(work.MessageId)).IsFalse();
  }

  [Test]
  public async Task Complete_SignalsChannelReaderCompletionAsync() {
    var writer = new InboxChannelWriter();

    writer.Complete();

    // After Complete, the channel reader's Completion task transitions
    // to a final state — waiting on it should not hang.
    await writer.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(writer.Reader.Completion.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task SignalNewInboxWorkAvailable_NoSubscribers_NoOpAsync() {
    var writer = new InboxChannelWriter();

    // Null-conditional invoke — no subscribers, no exception. Re-using the
    // writer afterwards still works normally.
    writer.SignalNewInboxWorkAvailable();

    // The lifecycle survived: writer is still usable for follow-on work.
    var ok = writer.TryWrite(_createWork());
    await Assert.That(ok).IsTrue();
  }

  [Test]
  public async Task SignalNewInboxWorkAvailable_WithSubscriber_FiresEventAsync() {
    var writer = new InboxChannelWriter();
    var tcs = new TaskCompletionSource();
    writer.OnNewInboxWorkAvailable += () => tcs.TrySetResult();

    writer.SignalNewInboxWorkAvailable();

    await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(tcs.Task.IsCompletedSuccessfully).IsTrue();
  }

  private static InboxWork _createWork() {
    var id = Guid.NewGuid();
    return new InboxWork {
      MessageId = id,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = new MessageId(id),
        Payload = JsonSerializer.SerializeToElement(new { Name = "test" }),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      },
      MessageType = "Test.TestEvent, Test",
    };
  }
}
