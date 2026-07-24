using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Direct tests for the internal <see cref="CascadeEnvelopeWrapper"/> — used by the
/// dispatcher cascade path to flip <see cref="MessageDispatchContext.IsDefaultDispatch"/>
/// to true without rebuilding the envelope. Every property + every method must
/// delegate to the inner envelope unchanged EXCEPT DispatchContext, which is a
/// fresh value with the default-dispatch flag set.
///
/// Why a wrapper instead of a clone: the envelope can carry heavy payloads, hop
/// lists, and receptor-invocation history; copying them per cascade would be
/// wasteful. The wrapper enforces "delegate everything else" — this test pins
/// that delegation so a future cleanup can't accidentally diverge a property.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatcher#cascade-default-dispatch</docs>
public class CascadeEnvelopeWrapperTests {

  [Test]
  public async Task DispatchContext_FlipsIsDefaultDispatchTrueAsync() {
    var inner = _makeInner(isDefaultDispatch: false);
    var wrapper = new CascadeEnvelopeWrapper(inner);

    await Assert.That(wrapper.DispatchContext.IsDefaultDispatch).IsTrue();
    // The inner stays unchanged — wrapper holds its own copy.
    await Assert.That(inner.DispatchContext.IsDefaultDispatch).IsFalse();
  }

  [Test]
  public async Task DispatchContext_PreservesModeAndSourceFromInnerAsync() {
    var inner = _makeInner(mode: DispatchModes.Outbox, source: MessageSource.Inbox);
    var wrapper = new CascadeEnvelopeWrapper(inner);

    await Assert.That(wrapper.DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(wrapper.DispatchContext.Source).IsEqualTo(MessageSource.Inbox);
  }

  [Test]
  public async Task Version_DelegatesToInnerAsync() {
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);

    await Assert.That(wrapper.Version).IsEqualTo(inner.Version);
  }

  [Test]
  public async Task MessageId_DelegatesToInnerAsync() {
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);

    await Assert.That(wrapper.MessageId).IsEqualTo(inner.MessageId);
  }

  [Test]
  public async Task Payload_DelegatesToInnerAsync() {
    // IMessageEnvelope.Payload is typed as object; the wrapper's getter
    // delegates each call to the inner. Compare by serialized value (Payload
    // is a JsonElement struct here — each access through the interface re-boxes,
    // so reference equality would compare boxes, not the underlying value).
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);

    await Assert.That(wrapper.Payload.ToString()).IsEqualTo(inner.Payload.ToString());
  }

  [Test]
  public async Task Hops_DelegatesToInnerReferenceAsync() {
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);

    // Same List reference — mutations on either side are visible to both.
    await Assert.That(wrapper.Hops).IsSameReferenceAs(inner.Hops);
  }

  [Test]
  public async Task AddHop_MutatesInnerHopsListAsync() {
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);
    var initialCount = inner.Hops.Count;

    wrapper.AddHop(new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown });

    // Wrapper.AddHop appends to the inner's hop list — the list grew by 1.
    await Assert.That(inner.Hops.Count).IsEqualTo(initialCount + 1);
  }

  [Test]
  public async Task ReceptorInvocations_NullOnEmptyInner_PopulatedAfterCreateAsync() {
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);

    await Assert.That(wrapper.ReceptorInvocations).IsNull();

    inner.GetOrCreateReceptorInvocations();
    await Assert.That(wrapper.ReceptorInvocations).IsNotNull();
  }

  [Test]
  public async Task GetOrCreateReceptorInvocations_PopulatesInnerListAsync() {
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);

    var list = wrapper.GetOrCreateReceptorInvocations();

    // After the call the inner exposes a non-null list (the wrapper delegated).
    await Assert.That(list).IsNotNull();
    await Assert.That(inner.ReceptorInvocations).IsNotNull();
  }

  [Test]
  public async Task GetMessageTimestamp_DelegatesToInnerHopTimestampAsync() {
    // The default GetMessageTimestamp returns Hops[0].Timestamp when hops exist,
    // else DateTimeOffset.UtcNow (which would drift between calls). Add a hop
    // with a known timestamp so the delegation is observable.
    var hopTime = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    var inner = _makeInner();
    inner.Hops.Add(new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Timestamp = hopTime,
    });
    var wrapper = new CascadeEnvelopeWrapper(inner);

    await Assert.That(wrapper.GetMessageTimestamp()).IsEqualTo(hopTime);
  }

  [Test]
  public async Task GetCorrelationId_DelegatesToInnerAsync() {
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);

    await Assert.That(wrapper.GetCorrelationId()).IsEqualTo(inner.GetCorrelationId());
  }

  [Test]
  public async Task GetCausationId_IsWrappedMessageId_NotInnerCausationAsync() {
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);

    // A child cascaded from the wrapped message is caused BY that message — its causation is the wrapped
    // envelope's own MessageId, not the wrapped message's causation (which is the grandparent). See
    // CascadeEnvelopeWrapperCausationTests for the full rationale; this replaces the old delegate-to-inner
    // assertion that flattened the causal chain one level up.
    await Assert.That(wrapper.GetCausationId()).IsEqualTo(inner.MessageId);
  }

  [Test]
  public async Task GetMetadata_DelegatesToInnerAsync() {
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);

    // Both should return the same null for an unknown key.
    await Assert.That(wrapper.GetMetadata("missing-key")).IsEqualTo(inner.GetMetadata("missing-key"));
  }

  [Test]
  public async Task GetCurrentScope_DelegatesToInnerAsync() {
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);

    await Assert.That(wrapper.GetCurrentScope()).IsEqualTo(inner.GetCurrentScope());
  }

  [Test]
  public async Task GetCurrentSecurityContext_DelegatesToInnerAsync() {
    var inner = _makeInner();
    var wrapper = new CascadeEnvelopeWrapper(inner);

#pragma warning disable CS0618
    await Assert.That(wrapper.GetCurrentSecurityContext()).IsEqualTo(inner.GetCurrentSecurityContext());
#pragma warning restore CS0618
  }

  private static MessageEnvelope<JsonElement> _makeInner(
    bool isDefaultDispatch = false,
    DispatchModes mode = DispatchModes.Local,
    MessageSource source = MessageSource.Local) =>
    new() {
      MessageId = new MessageId(Guid.NewGuid()),
      Payload = JsonSerializer.SerializeToElement(new { Name = "test" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext {
        Mode = mode,
        Source = source,
        IsDefaultDispatch = isDefaultDispatch,
      },
    };
}
