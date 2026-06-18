#pragma warning disable CA1707

using System.Text.Json;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the <see cref="OutboxMessage.Flags"/> / <see cref="InboxMessage.Flags"/>
/// transport plumbing (Slice 3'). Replaces the previous two boolean
/// columns (<c>is_composite</c>, <c>is_collective</c>) with the
/// <see cref="EventFlags"/> bitmask. The dispatcher stamps the bitmask
/// on the outbox row; the transport consumer worker preserves it onto
/// the inbox row. The projection runner checks individual flag bits to
/// decide which dispatch path to take.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class EventFlagsTransportTests {

  // ── OutboxMessage.Flags shape ─────────────────────────────────────────

  [Test]
  public async Task OutboxMessage_Flags_DefaultsToNoneAsync() {
    var msg = _newOutboxMessage();

    await Assert.That(msg.Flags).IsEqualTo(EventFlags.None)
      .Because("Default 0 == EventFlags.None. Every existing outbox-message builder that doesn't set Flags MUST continue routing through the ordinary per-row Apply path.");
  }

  [Test]
  public async Task OutboxMessage_Flags_AcceptsCollectiveAndCompositeAsync() {
    var collectiveOnly = _newOutboxMessage() with { Flags = EventFlags.Collective };
    var compositeOnly = _newOutboxMessage() with { Flags = EventFlags.Composite };
    var both = _newOutboxMessage() with { Flags = EventFlags.Collective | EventFlags.Composite };

    await Assert.That(collectiveOnly.Flags.HasFlag(EventFlags.Collective)).IsTrue();
    await Assert.That(collectiveOnly.Flags.HasFlag(EventFlags.Composite)).IsFalse();

    await Assert.That(compositeOnly.Flags.HasFlag(EventFlags.Composite)).IsTrue();
    await Assert.That(compositeOnly.Flags.HasFlag(EventFlags.Collective)).IsFalse();

    await Assert.That(both.Flags.HasFlag(EventFlags.Collective)).IsTrue();
    await Assert.That(both.Flags.HasFlag(EventFlags.Composite)).IsTrue();
  }

  // ── InboxMessage.Flags shape ──────────────────────────────────────────

  [Test]
  public async Task InboxMessage_Flags_DefaultsToNoneAsync() {
    var msg = _newInboxMessage();
    await Assert.That(msg.Flags).IsEqualTo(EventFlags.None);
  }

  [Test]
  public async Task InboxMessage_Flags_AcceptsCollectiveAndCompositeAsync() {
    var collective = _newInboxMessage() with { Flags = EventFlags.Collective };
    var composite = _newInboxMessage() with { Flags = EventFlags.Composite };

    await Assert.That(collective.Flags.HasFlag(EventFlags.Collective)).IsTrue();
    await Assert.That(composite.Flags.HasFlag(EventFlags.Composite)).IsTrue();
  }

  // ── Inline factories ──────────────────────────────────────────────────

  private static OutboxMessage _newOutboxMessage() => new() {
    MessageId = Guid.NewGuid(),
    Destination = "test",
    Envelope = _envelope(),
    Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
    EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement]], Whizbang.Core",
    MessageType = "System.Object, System.Private.CoreLib",
  };

  private static InboxMessage _newInboxMessage() => new() {
    MessageId = Guid.NewGuid(),
    HandlerName = "test-handler",
    Envelope = _envelope(),
    EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement]], Whizbang.Core",
    MessageType = "System.Object, System.Private.CoreLib",
  };

  private static MessageEnvelope<JsonElement> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = JsonDocument.Parse("{}").RootElement,
    DispatchContext = new MessageDispatchContext {
      Mode = DispatchModes.Outbox,
      Source = MessageSource.Outbox,
    },
    Hops = [],
  };
}
