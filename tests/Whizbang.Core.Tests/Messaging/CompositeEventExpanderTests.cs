#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the receive-side composite-expansion contract: each inner event
/// becomes its own envelope inheriting the composite's identity context
/// (StreamId, source stamps, hops shared by reference). Cap enforcement
/// stops yield without producing partial results.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#expansion</docs>
public class CompositeEventExpanderTests {

  [Test]
  public async Task Expand_YieldsOneEnvelopePerInnerEventAsync() {
    var composite = new _testComposite(_inner("J-001"), _inner("J-002"), _inner("J-003"));
    var envelope = _wrap(composite);

    var inners = CompositeEventExpander.Expand(envelope).ToList();

    await Assert.That(inners.Count).IsEqualTo(3);
    await Assert.That(((_innerEvent)inners[0].Payload).Id).IsEqualTo("J-001");
    await Assert.That(((_innerEvent)inners[1].Payload).Id).IsEqualTo("J-002");
    await Assert.That(((_innerEvent)inners[2].Payload).Id).IsEqualTo("J-003");
  }

  [Test]
  public async Task Expand_InheritsCompositeIdentityContextAsync() {
    var composite = new _testComposite(_inner("X"));
    var envelope = _wrap(composite, sourceServiceId: Guid.Parse("00000000-0000-0000-0000-000000000123"));

    var inners = CompositeEventExpander.Expand(envelope).ToList();
    var inner = inners.Single();

    await Assert.That(inner.SourceServiceId).IsEqualTo(envelope.SourceServiceId)
      .Because("Inner envelope MUST inherit source-service identity so per-source cursor logic at receiver continues to work uniformly for composite-fanout vs. ordinary delivery.");
    await Assert.That(inner.SourceCommitSequence).IsEqualTo(envelope.SourceCommitSequence);
    await Assert.That(inner.DispatchContext).IsEqualTo(envelope.DispatchContext);
    await Assert.That(inner.Hops).IsSameReferenceAs(envelope.Hops!)
      .Because("Hops shared by reference — composites are received together, audited together. Duplicating the hops list across 5K inner envelopes would burn memory for zero observability benefit.");
  }

  [Test]
  public async Task Expand_AssignsFreshMessageIdsPerInnerAsync() {
    var composite = new _testComposite(_inner("A"), _inner("B"));
    var envelope = _wrap(composite);

    var inners = CompositeEventExpander.Expand(envelope).ToList();

    await Assert.That(inners[0].MessageId).IsNotEqualTo(inners[1].MessageId)
      .Because("Inner events MUST have distinct MessageIds so receiver inbox dedup treats each as a distinct message; reusing the composite's MessageId would cause silent dedup of all-but-one inner event on redelivery.");
    await Assert.That(inners[0].MessageId).IsNotEqualTo(envelope.MessageId);
  }

  [Test]
  public async Task Expand_AtCap_LastInnerYielded_NoFailureAsync() {
    // Exactly MaxInnerEventsAllowed yields succeed.
    var inners = Enumerable.Range(0, 10).Select(i => _inner($"i-{i}")).ToArray();
    var composite = new _testComposite(inners) { MaxInnerEventsAllowedOverride = 10 };
    var envelope = _wrap(composite);

    var enumerated = CompositeEventExpander.Expand(envelope).ToList();

    await Assert.That(enumerated.Count).IsEqualTo(10);
  }

  [Test]
  public async Task Expand_OverCap_ThrowsCompositeInnerEventLimitExceededAsync() {
    // 11 inners + cap=10 → throws on the 11th yield.
    var inners = Enumerable.Range(0, 11).Select(i => _inner($"i-{i}")).ToArray();
    var composite = new _testComposite(inners) { MaxInnerEventsAllowedOverride = 10 };
    var envelope = _wrap(composite);

    Action act = () => _ = CompositeEventExpander.Expand(envelope).ToList();

    var ex = await Assert.That(act).Throws<CompositeInnerEventLimitExceededException>();
    await Assert.That(ex!.MaxInnerEventsAllowed).IsEqualTo(10);
    await Assert.That(ex.ObservedAtLeast).IsEqualTo(11);
  }

  [Test]
  public async Task Expand_NonCompositePayload_ThrowsClearMessageAsync() {
    var envelope = _wrapNonComposite();

    Action act = () => _ = CompositeEventExpander.Expand(envelope).ToList();

    var ex = await Assert.That(act).Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("ICompositeEvent");
  }

  [Test]
  public async Task Expand_NullInnerEvent_ThrowsInvalidOperationExceptionAsync() {
    // Producer's enumerator yields a null reference — the contract says every inner must be non-null.
    var envelope = new MessageEnvelope<ICompositeEvent> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = new _nullYieldingComposite(),
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
    };

    Action act = () => _ = CompositeEventExpander.Expand(envelope).ToList();

    var ex = await Assert.That(act).Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("null inner event")
      .Because("Null inner events corrupt the inbox if not caught at expansion — explicit throw beats silent fan-out of broken envelopes.");
  }

  [Test]
  public async Task ExpandGeneric_YieldsTypedEnvelopeForEachInnerAsync() {
    var composite = new _testComposite(_inner("J-001"), _inner("J-002"));
    var envelope = _wrap(composite);

    var inners = CompositeEventExpander.Expand<_innerEvent>(envelope).ToList();

    await Assert.That(inners.Count).IsEqualTo(2);
    await Assert.That(inners[0].Payload).IsTypeOf<_innerEvent>()
      .Because("Generic overload returns MessageEnvelope<TInner> — the payload is strongly typed for callers that know the inner type at compile time.");
    await Assert.That(inners[0].Payload.Id).IsEqualTo("J-001");
    await Assert.That(inners[1].Payload.Id).IsEqualTo("J-002");
    await Assert.That(inners[0].SourceServiceId).IsEqualTo(envelope.SourceServiceId)
      .Because("Generic build path inherits identity context just like the non-generic build path.");
    await Assert.That(inners[0].Hops).IsSameReferenceAs(envelope.Hops!);
    await Assert.That(inners[0].MessageId).IsNotEqualTo(envelope.MessageId)
      .Because("Fresh per-inner MessageId so inbox dedup treats each inner as distinct.");
  }

  [Test]
  public async Task ExpandGeneric_TypeMismatch_ThrowsInvalidOperationExceptionAsync() {
    // Producer yielded an inner event of a different runtime type than the caller-specified TInner.
    var composite = new _testComposite(_inner("X"));
    var envelope = _wrap(composite);

    // Caller asks for OtherInnerEvent, but the composite actually yields _innerEvent.
    Action act = () => _ = CompositeEventExpander.Expand<_otherInnerEvent>(envelope).ToList();

    var ex = await Assert.That(act).Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("not assignable")
      .Because("Type mismatch must surface as a clear error — silent skip would lose events; silent cast would corrupt envelopes.");
  }

  [Test]
  public async Task ExpandGeneric_OverCap_ThrowsCompositeInnerEventLimitExceededAsync() {
    var inners = Enumerable.Range(0, 11).Select(i => _inner($"i-{i}")).ToArray();
    var composite = new _testComposite(inners) { MaxInnerEventsAllowedOverride = 10 };
    var envelope = _wrap(composite);

    Action act = () => _ = CompositeEventExpander.Expand<_innerEvent>(envelope).ToList();

    var ex = await Assert.That(act).Throws<CompositeInnerEventLimitExceededException>();
    await Assert.That(ex!.MaxInnerEventsAllowed).IsEqualTo(10);
  }

  [Test]
  public async Task Expand_NullEnvelope_ThrowsArgumentNullExceptionAsync() {
    Action genericAct = () => _ = CompositeEventExpander.Expand<_innerEvent>(null!).ToList();
    Action nonGenericAct = () => _ = CompositeEventExpander.Expand((IMessageEnvelope)null!).ToList();

    await Assert.That(genericAct).Throws<ArgumentNullException>();
    await Assert.That(nonGenericAct).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task CompositeInnerEventLimitExceededException_CtorsExposeContextAsync() {
    // Parameterless + (message) + (message, inner) ctors are exercised so the SonarCloud
    // coverage on the rich Exception type doesn't drag down the new-code gate.
    var defaultInstance = new CompositeInnerEventLimitExceededException();
    await Assert.That(defaultInstance.MaxInnerEventsAllowed).IsEqualTo(0);

    var msgInstance = new CompositeInnerEventLimitExceededException("oops");
    await Assert.That(msgInstance.Message).IsEqualTo("oops");

    var inner = new InvalidOperationException("inner");
    var withInner = new CompositeInnerEventLimitExceededException("wrap", inner);
    await Assert.That(withInner.InnerException).IsSameReferenceAs(inner);
  }

  // ============================================================
  // Test composites + helpers
  // ============================================================

  private static _innerEvent _inner(string id) => new(id);

  private static MessageEnvelope<ICompositeEvent> _wrap(_testComposite composite, Guid? sourceServiceId = null) {
    return new MessageEnvelope<ICompositeEvent> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = composite,
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
      SourceServiceId = sourceServiceId ?? Guid.Parse("00000000-0000-0000-0000-000000000001"),
      SourceCommitSequence = 42,
    };
  }

  private static MessageEnvelope<_innerEvent> _wrapNonComposite() {
    return new MessageEnvelope<_innerEvent> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = new _innerEvent("not-composite"),
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
    };
  }

  private sealed record _innerEvent(string Id) : IEvent;

  private sealed record _otherInnerEvent(string Id) : IEvent;

  private sealed class _nullYieldingComposite : ICompositeEvent {
    public int MaxInnerEventsAllowed => 10;
    public IEnumerable<IMessage> InnerEvents {
      get {
        yield return null!;
      }
    }
  }

  private sealed class _testComposite : ICompositeEvent {
    public _testComposite(params _innerEvent[] inner) {
      _inner = inner;
    }
    private readonly _innerEvent[] _inner;
    public int? MaxInnerEventsAllowedOverride { get; init; }
    public int MaxInnerEventsAllowed => MaxInnerEventsAllowedOverride ?? 10_000;
    public IEnumerable<IMessage> InnerEvents => _inner;
  }
}
