using System.Text.Json;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage round 23 — closes gaps in <see cref="WorkChannelWriter"/>: a rejected write after
/// <see cref="WorkChannelWriter.Complete"/>, the drain-then-complete contract of Complete() itself,
/// bulk in-flight clearing, and the perspective-work-available signal.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/WorkChannelWriter.cs</code-under-test>
[Category("Messaging")]
public class WorkChannelWriterCoverageTests {

  private static OutboxWork _createWork(Guid? messageId = null) =>
    new() {
      MessageId = messageId ?? Guid.NewGuid(),
      Destination = "test-topic",
      Status = MessageProcessingStatus.Stored,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.New(),
        Payload = default,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      },
      EnvelopeType = "TestEnvelope",
      MessageType = "TestMessage",
      Attempts = 0
    };

  /// <summary>
  /// WorkChannelWriter hands work to the pipeline: an item accepted must be delivered, one rejected
  /// must be reported as rejected. If TryWrite ever started returning true (or throwing) after
  /// Complete(), a caller shutting down the pipeline would believe late work was queued when it was
  /// actually dropped on the floor — or the shutdown path itself would crash.
  /// </summary>
  [Test]
  public async Task TryWrite_AfterComplete_ReturnsFalseAsync() {
    var writer = new WorkChannelWriter();
    writer.Complete();
    var work = _createWork();

    var written = writer.TryWrite(work);

    await Assert.That(written).IsFalse()
      .Because("Complete() means no more work will be written; TryWrite must report the rejection, not silently accept it");
    await Assert.That(writer.IsInFlight(work.MessageId)).IsFalse()
      .Because("a write TryWrite reports as rejected must not be tracked as in-flight, or a message the pipeline never accepted would be treated as being processed");
  }

  /// <summary>
  /// Complete() must let existing work drain before consumers see the channel as finished; if it
  /// completed the reader immediately instead, work already queued but not yet read would be
  /// silently discarded rather than delivered.
  /// </summary>
  [Test]
  public async Task Complete_LetsQueuedWorkDrainBeforeTheReaderCompletesAsync() {
    var writer = new WorkChannelWriter();
    var work = _createWork();
    writer.TryWrite(work);

    writer.Complete();

    var read = await writer.Reader.ReadAsync();
    await Assert.That(read.MessageId).IsEqualTo(work.MessageId)
      .Because("work queued before Complete() must still be delivered to the consumer");

    await writer.Reader.Completion;
    await Assert.That(writer.Reader.Completion.IsCompletedSuccessfully).IsTrue()
      .Because("once the queued work is drained and no more will arrive, the reader must observe completion");
  }

  /// <summary>
  /// ClearInFlight is the bulk reset used on restart/recovery; if it left any tracked message behind,
  /// the polling path would believe that message is still being processed by a worker that no longer
  /// exists and would never re-queue it.
  /// </summary>
  [Test]
  public async Task ClearInFlight_RemovesEveryTrackedMessageAsync() {
    var writer = new WorkChannelWriter();
    var workA = _createWork();
    var workB = _createWork();
    writer.TryWrite(workA);
    writer.TryWrite(workB);

    writer.ClearInFlight();

    await Assert.That(writer.IsInFlight(workA.MessageId)).IsFalse();
    await Assert.That(writer.IsInFlight(workB.MessageId)).IsFalse()
      .Because("ClearInFlight must reset tracking for every message, not just the most recent one");
  }

  /// <summary>
  /// The perspective worker wakes up only because this event fires; a null-conditional invoke that
  /// silently no-ops (rather than actually calling the subscriber) would leave a genuinely subscribed
  /// worker asleep, and perspective work would stall until an unrelated poll happened to notice it.
  /// </summary>
  [Test]
  public async Task SignalNewPerspectiveWorkAvailable_InvokesTheSubscribedHandlerAsync() {
    var writer = new WorkChannelWriter();
    var invoked = false;
    writer.OnNewPerspectiveWorkAvailable += () => invoked = true;

    writer.SignalNewPerspectiveWorkAvailable();

    await Assert.That(invoked).IsTrue()
      .Because("the subscriber must actually be called, not just have its receiver evaluated");
  }
}
