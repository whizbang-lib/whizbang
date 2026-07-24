using System;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // underscores in test names by convention

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// <see cref="CascadeEnvelopeWrapper"/> wraps the source (parent) envelope while a receptor's returned
/// events are cascaded locally. When a child is cascaded from the wrapped message, the child's CAUSATION
/// must be the WRAPPED message's own id — not the wrapped message's causation (which is the grandparent).
/// This mirrors <see cref="CascadeContextFactory.FromEnvelope"/>, where causation = envelope.MessageId.
/// A wrong causation here flattens the causal chain one level up and loses the parent→child edge.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/CascadeEnvelopeWrapper.cs</code-under-test>
public class CascadeEnvelopeWrapperCausationTests {

  private sealed record TestMessage(string Value) : IMessage;

  private static MessageEnvelope<TestMessage> _envelopeWith(MessageId messageId, MessageId ownCausation) {
    return new MessageEnvelope<TestMessage> {
      MessageId = messageId,
      Payload = new TestMessage("x"),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = CorrelationId.New(),
          CausationId = ownCausation,
          ServiceInstance = ServiceInstanceInfo.Unknown,
        },
      ],
    };
  }

  [Test]
  public async Task GetCausationId_ReturnsWrappedMessageId_NotGrandparentCausationAsync() {
    // Arrange: a parent envelope whose OWN causation is the grandparent (a different id than its MessageId).
    var wrappedMessageId = MessageId.From(Guid.CreateVersion7());
    var grandparentCausation = MessageId.From(Guid.CreateVersion7());
    var inner = _envelopeWith(wrappedMessageId, grandparentCausation);

    var wrapper = new CascadeEnvelopeWrapper(inner);

    // Act
    var childCausation = wrapper.GetCausationId();

    // Assert: a child cascaded from the wrapper is caused by the WRAPPED message, so causation == its id.
    await Assert.That(childCausation).IsEqualTo(wrappedMessageId)
      .Because("A child cascaded from the wrapped message must be caused BY that message (its MessageId), not by the grandparent.");
    await Assert.That(childCausation).IsNotEqualTo(grandparentCausation)
      .Because("Returning the wrapped message's own causation would skip the parent and point at the grandparent.");
  }
}
