using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The claim loop needs to know how much work it is already holding before it claims more, and it
/// needs a way out of a permanent stall. Both come from the in-flight set the channel already keeps
/// — it simply did not expose a count, so nothing could gate on it.
/// </summary>
[Category("Messaging")]
public class InboxChannelWriterOutstandingTests {

  [Test]
  public async Task InFlightCount_ReflectsWorkHandedToTheChannelAsync() {
    var writer = new InboxChannelWriter();

    await Assert.That(writer.InFlightCount).IsEqualTo(0);

    await writer.WriteAsync(_work());
    await writer.WriteAsync(_work());

    await Assert.That(writer.InFlightCount).IsEqualTo(2);
  }

  [Test]
  public async Task InFlightCount_DropsAsWorkCompletesAsync() {
    var writer = new InboxChannelWriter();
    var first = _work();
    await writer.WriteAsync(first);
    await writer.WriteAsync(_work());

    writer.RemoveInFlight(first.MessageId);

    await Assert.That(writer.InFlightCount).IsEqualTo(1);
  }

  [Test]
  public async Task PruneInFlightOlderThan_RemovesLapsedEntriesAndReportsHowManyAsync() {
    var writer = new InboxChannelWriter();
    await writer.WriteAsync(_work());
    await writer.WriteAsync(_work());

    // Age zero means "everything tracked so far is older than this", which exercises the prune
    // deterministically without sleeping.
    var pruned = writer.PruneInFlightOlderThan(TimeSpan.Zero);

    await Assert.That(pruned).IsEqualTo(2);
    await Assert.That(writer.InFlightCount).IsEqualTo(0);
  }

  [Test]
  public async Task PruneInFlightOlderThan_LeavesFreshEntriesAloneAsync() {
    var writer = new InboxChannelWriter();
    await writer.WriteAsync(_work());

    // Work that is still comfortably inside its lease must NOT be dropped — doing so would let the
    // claim loop take on more while the original rows are still legitimately being processed,
    // which is the very over-commitment this whole mechanism exists to prevent.
    var pruned = writer.PruneInFlightOlderThan(TimeSpan.FromHours(1));

    await Assert.That(pruned).IsEqualTo(0);
    await Assert.That(writer.InFlightCount).IsEqualTo(1);
  }

  private static InboxWork _work() {
    // MessageId enforces UUIDv7 — Guid.NewGuid() produces v4 and is rejected at the boundary.
    var messageId = MessageId.New();
    var id = messageId.Value;
    return new InboxWork {
      MessageId = id,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = messageId,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [],
        Payload = JsonDocument.Parse("{}").RootElement
      },
      MessageType = "test-message"
    };
  }
}
