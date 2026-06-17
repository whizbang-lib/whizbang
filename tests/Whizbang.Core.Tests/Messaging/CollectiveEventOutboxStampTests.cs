#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Producer-side stamping: when the dispatcher serializes a payload
/// implementing <see cref="ICollectiveEvent"/>, the resulting
/// <see cref="OutboxMessage"/> carries <c>IsCollective = true</c>. The
/// consumer worker preserves the flag onto the <see cref="InboxMessage"/>;
/// the projection runner (Slice 7) reads it from the inbox row to branch
/// onto the collective Apply path without re-running the payload type check.
/// </summary>
/// <remarks>
/// Mirrors the existing <c>CompositeEventOutboxStampTests</c> shape (W3
/// slice 9) — same defaults-to-false + settable-to-true pattern for the
/// new <c>IsCollective</c> flag.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public class CollectiveEventOutboxStampTests {

  [Test]
  public async Task OutboxMessage_IsCollective_DefaultsToFalseAsync() {
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

    await Assert.That(msg.IsCollective).IsFalse()
      .Because("Default false so existing OutboxMessage construction (pre-Slice 3) doesn't accidentally route to the collective Apply path in Slice 7.");
  }

  [Test]
  public async Task OutboxMessage_IsCollective_CanBeSetToTrueAsync() {
    var msg = new OutboxMessage {
      MessageId = Guid.NewGuid(),
      Envelope = _emptyJsonEnvelope(),
      Metadata = new Whizbang.Core.Observability.EnvelopeMetadata {
        MessageId = Whizbang.Core.ValueObjects.MessageId.New(),
        Hops = []
      },
      EnvelopeType = "MessageEnvelope<MyCollective>, MyAssembly",
      MessageType = "MyCollective, MyAssembly",
      IsCollective = true,
    };

    await Assert.That(msg.IsCollective).IsTrue();
  }

  [Test]
  public async Task InboxMessage_IsCollective_DefaultsToFalseAsync() {
    var msg = new InboxMessage {
      MessageId = Guid.NewGuid(),
      HandlerName = "TestHandler",
      Envelope = _emptyJsonEnvelope(),
      EnvelopeType = "MessageEnvelope<X>, MyAssembly",
      MessageType = "X, MyAssembly",
    };

    await Assert.That(msg.IsCollective).IsFalse()
      .Because("Receive-side mirrors producer-side: default false; Slice 7 only branches when the flag is explicitly set by the transport consumer.");
  }

  [Test]
  public async Task InboxMessage_IsCollective_CanBeSetToTrueAsync() {
    var msg = new InboxMessage {
      MessageId = Guid.NewGuid(),
      HandlerName = "TestHandler",
      Envelope = _emptyJsonEnvelope(),
      EnvelopeType = "MessageEnvelope<MyCollective>, MyAssembly",
      MessageType = "MyCollective, MyAssembly",
      IsCollective = true,
    };

    await Assert.That(msg.IsCollective).IsTrue();
  }

  [Test]
  public async Task IsCollective_IsIndependentOfIsCompositeAsync() {
    // The two flags are orthogonal: a payload could (in theory, but
    // unusually) implement both interfaces. The runtime treats them
    // separately — composite triggers receiver expansion, collective
    // triggers the set-based Apply path. Test that the record allows
    // both independently.
    var msg = new OutboxMessage {
      MessageId = Guid.NewGuid(),
      Envelope = _emptyJsonEnvelope(),
      Metadata = new Whizbang.Core.Observability.EnvelopeMetadata {
        MessageId = Whizbang.Core.ValueObjects.MessageId.New(),
        Hops = []
      },
      EnvelopeType = "MessageEnvelope<X>, MyAssembly",
      MessageType = "X, MyAssembly",
      IsComposite = true,
      IsCollective = true,
    };

    await Assert.That(msg.IsComposite).IsTrue();
    await Assert.That(msg.IsCollective).IsTrue();
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
