// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The opt-in ordering key on a collective event (#963). Collectives that share a key, in one scope, share one
/// stream, and so one <c>__collective__</c> sink stream, which the perspective worker applies in commit order. Without
/// a key every collective keeps its own stream, as before.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("CollectiveEvents")]
public class CollectiveOrderingKeyTests {
  private sealed record ActivateEvent : CollectiveEventBase {
    public string Chosen { get; init; } = "";
  }

  private sealed record DeactivateEvent : CollectiveEventBase;

  private sealed record HandWrittenCollective : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  [Test]
  public async Task WithoutAnOrderingKey_TheEventKeepsTheStreamItWasGivenAsync() {
    var streamId = TrackedGuid.New().Value;
    var evt = new ActivateEvent { StreamId = streamId, Scope = new TenantCollectiveScope("t-1") };

    await Assert.That(evt.OrderingKey).IsNull();
    await Assert.That(evt.StreamId).IsEqualTo(streamId)
      .Because("An unkeyed collective is its own single-event stream, exactly as before the key existed.");
  }

  [Test]
  public async Task WithAnOrderingKey_TheStreamIsDerivedFromTheScopeAndTheKeyAsync() {
    var scope = new TenantCollectiveScope("t-1");
    var evt = new ActivateEvent { Scope = scope, OrderingKey = "activation:family-7" };

    await Assert.That(evt.StreamId).IsEqualTo(CollectiveOrdering.StreamIdFor(scope, "activation:family-7"));
  }

  [Test]
  public async Task WithAnOrderingKey_AMintedStreamIdIsIgnoredAsync() {
    var evt = new ActivateEvent { Scope = new TenantCollectiveScope("t-1"), OrderingKey = "k" };
    var derived = evt.StreamId;

    evt.StreamId = TrackedGuid.New().Value;

    await Assert.That(evt.StreamId).IsEqualTo(derived)
      .Because("The dispatcher mints a stream id for every collective at publish. A keyed one must stay on its key's "
        + "stream whatever is written to it, or two collectives sharing the key would again be two streams.");
  }

  [Test]
  public async Task TwoEventTypes_SharingAKeyInOneScope_ShareAStreamAsync() {
    var scope = new TenantCollectiveScope("t-1");
    var deactivate = new DeactivateEvent { Scope = scope, OrderingKey = "family-7" };
    var activate = new ActivateEvent { Scope = scope, OrderingKey = "family-7", Chosen = "b" };

    await Assert.That(activate.StreamId).IsEqualTo(deactivate.StreamId)
      .Because("The two halves of a swap are different collective types; ordering them is the point of the key.");
  }

  [Test]
  public async Task TheSameKey_InTwoScopes_IsTwoStreamsAsync() {
    var a = new ActivateEvent { Scope = new TenantCollectiveScope("t-1"), OrderingKey = "family-7" };
    var b = new ActivateEvent { Scope = new TenantCollectiveScope("t-2"), OrderingKey = "family-7" };

    await Assert.That(a.StreamId).IsNotEqualTo(b.StreamId)
      .Because("Two tenants' collectives touch disjoint rows; ordering them against each other would only serialize them.");
  }

  [Test]
  public async Task TwoKeys_InOneScope_AreTwoStreamsAsync() {
    var scope = new TenantCollectiveScope("t-1");
    await Assert.That(CollectiveOrdering.StreamIdFor(scope, "family-7"))
      .IsNotEqualTo(CollectiveOrdering.StreamIdFor(scope, "family-8"));
  }

  [Test]
  public async Task TheDerivation_IsPinnedAsync() {
    var derived = CollectiveOrdering.StreamIdFor(new TenantCollectiveScope("t-1"), "activation:family-7");

    await Assert.That(derived).IsEqualTo(Guid.Parse("e8ba46c1-e34b-833b-a0b7-e08fd04f50e4"))
      .Because("Every service that emits or receives a keyed collective must compute the same stream. Changing the "
        + "derivation splits one key into two streams across a deploy, so it is a compatibility contract.");
  }

  [Test]
  public async Task TheDerivedStream_IsAVersion8GuidAsync() {
    var derived = CollectiveOrdering.StreamIdFor(new TenantCollectiveScope("t-1"), "k");
    await Assert.That(derived.Version).IsEqualTo(8)
      .Because("A name-derived id is not time-ordered; marking it version 8 keeps it from reading as a minted UUIDv7.");
  }

  [Test]
  public async Task AHandWrittenCollective_HasNoOrderingKeyAsync() {
    ICollectiveEvent evt = new HandWrittenCollective { Scope = new TenantCollectiveScope("t-1") };
    await Assert.That(evt.OrderingKey).IsNull();
  }

  [Test]
  public async Task StreamIdFor_NullScope_ThrowsAsync() {
    await Assert.That(() => CollectiveOrdering.StreamIdFor(null!, "k")).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task StreamIdFor_BlankKey_ThrowsAsync() {
    await Assert.That(() => CollectiveOrdering.StreamIdFor(new TenantCollectiveScope("t-1"), " "))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task ABlankOrderingKey_IsNoKeyAsync() {
    var streamId = TrackedGuid.New().Value;
    var evt = new ActivateEvent { StreamId = streamId, Scope = new TenantCollectiveScope("t-1"), OrderingKey = "" };

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
  }
}
