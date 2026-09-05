using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// What a composite does when one of its inner events cannot be serialized.
/// <para>
/// The two atomicity modes exist to answer a question the framework cannot answer for the caller:
/// is a partially-expanded composite better than none? For an atomic composite it is not — the
/// inner events are a unit, so half of them landing in the inbox is a corrupt outcome and the whole
/// expansion has to fail. For an independent one it is — the inner events are unrelated work that
/// happened to travel together, so dropping the unserializable one and delivering the rest loses
/// strictly less.
/// </para>
/// <para>
/// The drop is logged once rather than per event. A composite carrying thousands of inner events
/// with a systematic serialization fault would otherwise emit thousands of identical lines, which
/// is how a real signal gets buried.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/CompositeInboxFanout.cs</code-under-test>
public class CompositeFanoutAtomicityTests {

  private sealed record InnerEvent(string Id) : IEvent;

  private sealed class AtomicComposite(params InnerEvent[] inner) : ICompositeEvent {
    public int MaxInnerEventsAllowed => 10_000;
    public IEnumerable<IMessage> InnerEvents => inner;
    public FanoutAtomicity Atomicity => FanoutAtomicity.Atomic;
  }

  private sealed class IndependentComposite(params InnerEvent[] inner) : ICompositeEvent {
    public int MaxInnerEventsAllowed => 10_000;
    public IEnumerable<IMessage> InnerEvents => inner;
    public FanoutAtomicity Atomicity => FanoutAtomicity.Independent;
  }

  /// <summary>
  /// Accepts every inner event except the one whose id is refused, which it fails on.
  /// </summary>
  /// <remarks>
  /// Synthesizes its own result rather than delegating to the real serializer: the fixture's inner
  /// event type is not in the source-generated JSON context, so a real serialize would fail for all
  /// three and the test could not tell "one child was dropped" from "none of them serialized".
  /// </remarks>
  private sealed class SelectivelyFailingSerializer(string refusedId) : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      if (envelope.Payload is InnerEvent refused && refused.Id == refusedId) {
        throw new JsonException($"cannot serialize inner event '{refused.Id}'");
      }
      var json = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { id = (envelope.Payload as InnerEvent)?.Id }),
        Hops = [.. envelope.Hops],
        DispatchContext = envelope.DispatchContext,
      };
      return new SerializedEnvelope(json, "test-envelope", typeof(InnerEvent).FullName!);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName)
      => throw new NotSupportedException("fan-out never deserializes");
  }

  private static (IMessageEnvelope Source, IServiceProvider Scope) _build(string refusedId) {
    var hop = new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
    };
    var source = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { v = 1 }),
      Hops = [hop],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Inbox },
    };
    var services = new ServiceCollection();
    services.AddSingleton<IEnvelopeSerializer>(new SelectivelyFailingSerializer(refusedId));
    return (source, services.BuildServiceProvider());
  }

  [Test]
  public async Task AnAtomicComposite_FailsEntirelyWhenOneChildCannotSerializeAsync() {
    var (source, scope) = _build(refusedId: "b");
    var composite = new AtomicComposite(new InnerEvent("a"), new InnerEvent("b"), new InnerEvent("c"));

    var result = CompositeInboxFanout.TryExpand(composite, source, scope);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Failed)
      .Because("the inner events of an atomic composite are a unit — delivering two of three is a "
             + "corrupt outcome, not a partial success");
    await Assert.That(result.Children).IsEmpty()
      .Because("a failed atomic expansion must not leave any child behind in the inbox");
  }

  [Test]
  public async Task AnIndependentComposite_DropsTheBadChildAndKeepsTheRestAsync() {
    var (source, scope) = _build(refusedId: "b");
    var composite = new IndependentComposite(new InnerEvent("a"), new InnerEvent("b"), new InnerEvent("c"));

    var result = CompositeInboxFanout.TryExpand(composite, source, scope);

    await Assert.That(result.Outcome).IsNotEqualTo(CompositeInboxFanout.FanoutOutcome.Failed)
      .Because("these inner events are unrelated work that happened to travel together, so losing "
             + "one must not cost the others");
    await Assert.That(result.Children.Count).IsEqualTo(2)
      .Because("two of three serialize, and delivering them loses strictly less than delivering none");
  }
}
