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

/// <summary>
/// Covers <see cref="InboxChannelWriter.TryWrite"/>'s rejection path — untouched by the sibling
/// <c>InboxChannelWriterTests</c> / <c>InboxChannelWriterInFlightBoundingTests</c>, which only ever
/// call <c>TryWrite</c> on a channel that is still open. An unbounded channel's <c>TryWrite</c>
/// only ever returns <c>false</c> once the writer side has been completed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/InboxChannelWriter.cs</code-under-test>
public class InboxChannelWriterCoverageTests {

  [Test]
  public async Task TryWrite_AfterComplete_ReturnsFalseAndDoesNotTrackInFlightAsync() {
    // A worker that races Complete() against a final TryWrite (shutdown draining) must get a clean
    // false, and — because the write never reached the channel — the message must not be recorded
    // as in-flight, or ShouldRenewLease/IsInFlight would report on work that was never queued.
    var writer = new InboxChannelWriter();
    writer.Complete();
    var work = _createWork();

    var accepted = writer.TryWrite(work);

    await Assert.That(accepted).IsFalse();
    await Assert.That(writer.IsInFlight(work.MessageId)).IsFalse()
      .Because("the in-flight tracking must only happen for a write the channel actually accepted.");
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
