#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Producer-side stamping: when the dispatcher serializes a payload
/// implementing <see cref="ICompositeEvent"/>, the resulting
/// <see cref="OutboxMessage"/> carries <c>IsComposite = true</c>. The
/// receiver-side expansion (slice 10) reads this flag from the inbox row
/// to trigger inner-event enumeration without re-running the payload type
/// check.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#producer</docs>
public class CompositeEventOutboxStampTests {

  [Test]
  public async Task OutboxMessage_IsComposite_DefaultsToFalseAsync() {
    var msg = new OutboxMessage {
      MessageId = Guid.NewGuid(),
      Envelope = _emptyJsonEnvelope(),
      Metadata = new Whizbang.Core.Observability.EnvelopeMetadata {
        MessageId = Whizbang.Core.ValueObjects.MessageId.New(),
        Hops = []
      },
      EnvelopeType = "MessageEnvelope<X>, MyAssembly",
      MessageType = "X, MyAssembly",
    };

    await Assert.That(msg.IsComposite).IsFalse()
      .Because("Default to false so existing OutboxMessage construction (pre-W3 slice 9) doesn't accidentally trigger composite-expansion paths in slice 10.");
  }

  [Test]
  public async Task OutboxMessage_IsComposite_CanBeSetToTrueAsync() {
    var msg = new OutboxMessage {
      MessageId = Guid.NewGuid(),
      Envelope = _emptyJsonEnvelope(),
      Metadata = new Whizbang.Core.Observability.EnvelopeMetadata {
        MessageId = Whizbang.Core.ValueObjects.MessageId.New(),
        Hops = []
      },
      EnvelopeType = "MessageEnvelope<MyComposite>, MyAssembly",
      MessageType = "MyComposite, MyAssembly",
      IsComposite = true,
    };

    await Assert.That(msg.IsComposite).IsTrue();
  }

  [Test]
  public async Task InboxMessage_IsComposite_DefaultsToFalseAsync() {
    var msg = new InboxMessage {
      MessageId = Guid.NewGuid(),
      HandlerName = "TestHandler",
      Envelope = _emptyJsonEnvelope(),
      EnvelopeType = "MessageEnvelope<X>, MyAssembly",
      MessageType = "X, MyAssembly",
    };

    await Assert.That(msg.IsComposite).IsFalse()
      .Because("Default to false on the receive side too; slice 10 expansion is opt-in via this flag.");
  }

  [Test]
  public async Task InboxMessage_IsComposite_CanBeSetToTrueAsync() {
    var msg = new InboxMessage {
      MessageId = Guid.NewGuid(),
      HandlerName = "TestHandler",
      Envelope = _emptyJsonEnvelope(),
      EnvelopeType = "MessageEnvelope<MyComposite>, MyAssembly",
      MessageType = "MyComposite, MyAssembly",
      IsComposite = true,
    };

    await Assert.That(msg.IsComposite).IsTrue();
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static Whizbang.Core.Observability.MessageEnvelope<System.Text.Json.JsonElement> _emptyJsonEnvelope() =>
    new() {
      DispatchContext = new Whizbang.Core.Observability.MessageDispatchContext {
        Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox,
        Source = Whizbang.Core.Messaging.MessageSource.Outbox
      },
      MessageId = Whizbang.Core.ValueObjects.MessageId.New(),
      Payload = System.Text.Json.JsonDocument.Parse("{}").RootElement,
      Hops = [new Whizbang.Core.Observability.MessageHop {
        Type = Whizbang.Core.Observability.HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = Whizbang.Core.Observability.ServiceInstanceInfo.Unknown
      }],
    };
}
