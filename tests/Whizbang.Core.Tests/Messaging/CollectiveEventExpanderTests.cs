#pragma warning disable CA1707

using System.Collections.Generic;
using System.Linq;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the contract of <see cref="CollectiveEventExpander"/>. Mirrors
/// the shape of the existing <c>CompositeEventExpanderTests</c> — fan-out
/// invariants (matched-set → N markers), defensive cap, identity
/// preservation (per-marker fresh MessageId, shared Hops list).
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public class CollectiveEventExpanderTests {

  // ── Typed expansion ────────────────────────────────────────────────────

  [Test]
  public async Task Expand_Typed_YieldsOneMarkerPerMatchedStreamIdAsync() {
    var ids = new Guid[] {
      TrackedGuid.NewMedo(),
      TrackedGuid.NewMedo(),
      TrackedGuid.NewMedo(),
    };
    var evt = new _archiveCollective(new _tenantScope("t-1"), ids);
    var envelope = _envelopeFor(evt);

    var markers = CollectiveEventExpander.Expand(envelope).ToList();

    await Assert.That(markers).Count().IsEqualTo(ids.Length);
    for (var i = 0; i < ids.Length; i++) {
      await Assert.That(markers[i].Payload.StreamId).IsEqualTo(ids[i])
        .Because("Marker order MUST preserve MatchedStreamIds order so consumers can rely on it for deterministic per-stream ordering.");
      await Assert.That(markers[i].Payload.SourceEvent).IsSameReferenceAs(evt)
        .Because("Every marker references the SAME source collective event — no defensive copy. Consumers may read scope / payload from the source.");
    }
  }

  [Test]
  public async Task Expand_Typed_EachMarkerGetsFreshMessageIdAsync() {
    var evt = new _archiveCollective(
      new _tenantScope("t-1"),
      [TrackedGuid.NewMedo(), TrackedGuid.NewMedo()]);
    var envelope = _envelopeFor(evt);

    var markers = CollectiveEventExpander.Expand(envelope).ToList();

    await Assert.That(markers[0].MessageId).IsNotEqualTo(markers[1].MessageId)
      .Because("Inbox dedup operates on MessageId; sibling markers must NOT collide.");
    await Assert.That(markers[0].MessageId).IsNotEqualTo(envelope.MessageId)
      .Because("Marker MessageId must NOT equal the source envelope's; otherwise dedup would discard the marker as a re-delivery of the source.");
  }

  [Test]
  public async Task Expand_Typed_HopsArePreservedByReferenceAcrossMarkersAsync() {
    var evt = new _archiveCollective(
      new _tenantScope("t-1"),
      [TrackedGuid.NewMedo(), TrackedGuid.NewMedo()]);
    var envelope = _envelopeFor(evt);

    var markers = CollectiveEventExpander.Expand(envelope).ToList();

    await Assert.That(ReferenceEquals(markers[0].Hops, envelope.Hops)).IsTrue()
      .Because("Memory-shape invariant — hops list is shared by reference across the source and every marker so high-fan-out collectives don't duplicate the journey audit.");
    await Assert.That(ReferenceEquals(markers[1].Hops, markers[0].Hops)).IsTrue()
      .Because("Same reference across siblings too — proves the optimization holds beyond just source-to-first-marker.");
  }

  [Test]
  public async Task Expand_Typed_EmptyMatchedSet_YieldsNoMarkersAsync() {
    var evt = new _archiveCollective(new _tenantScope("t-1"), []);
    var envelope = _envelopeFor(evt);

    var markers = CollectiveEventExpander.Expand(envelope).ToList();

    await Assert.That(markers).IsEmpty()
      .Because("Empty matched-set is well-formed (the collective applied to zero rows) — expansion produces zero markers, never throws.");
  }

  // ── Defensive cap ──────────────────────────────────────────────────────

  [Test]
  public async Task Expand_OverCap_ThrowsCollectiveExpansionLimitExceededAsync() {
    var ids = Enumerable.Range(0, 5).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();
    var evt = new _smallCapCollective(new _tenantScope("t-1"), ids); // cap = 3
    var envelope = _envelopeFor(evt);

    await Assert.That(() => CollectiveEventExpander.Expand(envelope).ToList())
      .ThrowsExactly<CollectiveExpansionLimitExceededException>()
      .Because("Matched-set of 5 exceeds the type's cap of 3 — expander MUST throw at the head with NO partial yield.");
  }

  [Test]
  public async Task Expand_OverCap_DoesNotYieldPartialResultAsync() {
    var ids = Enumerable.Range(0, 5).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();
    var evt = new _smallCapCollective(new _tenantScope("t-1"), ids);
    var envelope = _envelopeFor(evt);

    var partial = new List<MessageEnvelope<CollectivePerStreamMarker>>();
    try {
      foreach (var marker in CollectiveEventExpander.Expand(envelope)) {
        partial.Add(marker);
      }
    } catch (CollectiveExpansionLimitExceededException) {
      // expected — partial must still be empty.
    }

    await Assert.That(partial).IsEmpty()
      .Because("No-partial-yield invariant — consumers downstream assume the stream of markers is a faithful expansion of the full matched-set or nothing.");
  }

  // ── Non-generic expansion ─────────────────────────────────────────────

  [Test]
  public async Task Expand_NonGeneric_WhenPayloadIsCollective_YieldsMarkersAsync() {
    var evt = new _archiveCollective(
      new _tenantScope("t-1"),
      [TrackedGuid.NewMedo(), TrackedGuid.NewMedo()]);
    IMessageEnvelope envelope = _envelopeFor(evt);

    var markers = CollectiveEventExpander.Expand(envelope).ToList();

    await Assert.That(markers).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Expand_NonGeneric_WhenPayloadIsNotCollective_ThrowsInvalidOperationAsync() {
    var envelope = new MessageEnvelope<_notACollective> {
      DispatchContext = _dispatchContext(),
      MessageId = MessageId.New(),
      Payload = new _notACollective("hello"),
      Hops = _hops(),
    };

    await Assert.That(() => CollectiveEventExpander.Expand(envelope).ToList())
      .ThrowsExactly<InvalidOperationException>()
      .Because("Defensive guard — non-generic Expand requires callers to have already checked Payload is ICollectiveEvent. Better a clear throw than mysterious empty yield.");
  }

  // ── ICollectiveEvent.MaxExpandedInnersAllowed default ──────────────────

  [Test]
  public async Task ICollectiveEvent_MaxExpandedInnersAllowed_DefaultIs10KAsync() {
    ICollectiveEvent evt = new _archiveCollective(new _tenantScope("t"), []);

    await Assert.That(evt.MaxExpandedInnersAllowed).IsEqualTo(10_000)
      .Because("Parity with ICompositeEvent.MaxInnerEventsAllowed default. Producers raising past 10K MUST override and document why.");
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed record _tenantScope(string TenantId) : ICollectiveScope {
    public string ScopeKind => "tenant";
  }

  private sealed record _archiveCollective(ICollectiveScope Scope, IReadOnlyList<Guid> MatchedStreamIds)
    : ICollectiveEvent;

  /// <summary>Override raises the cap to 3 so the over-cap tests can use small fixtures.</summary>
  private sealed record _smallCapCollective(ICollectiveScope Scope, IReadOnlyList<Guid> MatchedStreamIds)
    : ICollectiveEvent {
    public int MaxExpandedInnersAllowed => 3;
  }

  private sealed record _notACollective(string Text) : IMessage;

  // ── Envelope plumbing ──────────────────────────────────────────────────

  private static MessageEnvelope<TCollective> _envelopeFor<TCollective>(TCollective evt)
    where TCollective : ICollectiveEvent {
    return new MessageEnvelope<TCollective> {
      DispatchContext = _dispatchContext(),
      MessageId = MessageId.New(),
      Payload = evt,
      Hops = _hops(),
    };
  }

  private static MessageDispatchContext _dispatchContext() => new() {
    Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox,
    Source = MessageSource.Outbox,
  };

  private static List<MessageHop> _hops() => [
    new() {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      ServiceInstance = ServiceInstanceInfo.Unknown,
    }
  ];
}
