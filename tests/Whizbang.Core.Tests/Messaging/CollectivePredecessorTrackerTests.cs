using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The predecessor link on a keyed collective (#1003): each collective a process publishes on an ordering key names the
/// one it published on that key before it, by id and type, so a receiver can apply them in the order they were sent.
/// </summary>
/// <docs>fundamentals/messaging/collective-events#ordering-across-services</docs>
[Category("CollectiveEvents")]
public class CollectivePredecessorTrackerTests {
  private sealed record ActivateEvent : CollectiveEventBase;

  private sealed record DeactivateEvent : CollectiveEventBase;

  private sealed record HandWrittenCollective : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  private static readonly TenantCollectiveScope _scope = new("t-1");

  [Test]
  public async Task Stamp_FirstCollectiveOnAKey_CarriesNoLinkAsync() {
    var tracker = new CollectivePredecessorTracker();
    var first = new ActivateEvent { Scope = _scope, OrderingKey = "family-7" };

    tracker.Stamp(first, TrackedGuid.New().Value);

    await Assert.That(first.PredecessorId).IsNull();
    await Assert.That(first.PredecessorType).IsNull();
  }

  /// <summary>The second collective on a key names the first, whatever its type: the two halves of a swap differ.</summary>
  [Test]
  public async Task Stamp_SecondCollectiveOnAKey_CarriesTheFirstAsync() {
    var tracker = new CollectivePredecessorTracker();
    var deactivate = new DeactivateEvent { Scope = _scope, OrderingKey = "family-7" };
    var activate = new ActivateEvent { Scope = _scope, OrderingKey = "family-7" };
    var deactivateId = TrackedGuid.New().Value;

    tracker.Stamp(deactivate, deactivateId);
    tracker.Stamp(activate, TrackedGuid.New().Value);

    await Assert.That(activate.PredecessorId).IsEqualTo(deactivateId);
    await Assert.That(activate.PredecessorType).IsEqualTo(TypeNameFormatter.Format(typeof(DeactivateEvent)));
    await Assert.That(((ICollectiveEvent)activate).PredecessorId).IsEqualTo(deactivateId)
      .Because("the receiver reads the link through the interface");
  }

  [Test]
  public async Task Stamp_CollectivesOnDifferentKeys_AreNotLinkedAsync() {
    var tracker = new CollectivePredecessorTracker();
    var one = new ActivateEvent { Scope = _scope, OrderingKey = "family-7" };
    var other = new ActivateEvent { Scope = _scope, OrderingKey = "family-8" };

    tracker.Stamp(one, TrackedGuid.New().Value);
    tracker.Stamp(other, TrackedGuid.New().Value);

    await Assert.That(other.PredecessorId).IsNull();
  }

  /// <summary>Publishing the same collective again does not link it to itself.</summary>
  [Test]
  public async Task Stamp_TheSameCollectiveAgain_IsNotItsOwnPredecessorAsync() {
    var tracker = new CollectivePredecessorTracker();
    var evt = new ActivateEvent { Scope = _scope, OrderingKey = "family-7" };
    var id = TrackedGuid.New().Value;

    tracker.Stamp(evt, id);
    tracker.Stamp(evt, id);

    await Assert.That(evt.PredecessorId).IsNull();
  }

  /// <summary>A link already on the collective is kept, so a collective sent on again keeps the order it was sent in.</summary>
  [Test]
  public async Task Stamp_ACollectiveAlreadyLinked_KeepsItsLinkAsync() {
    var tracker = new CollectivePredecessorTracker();
    var original = TrackedGuid.New().Value;
    tracker.Stamp(new ActivateEvent { Scope = _scope, OrderingKey = "family-7" }, TrackedGuid.New().Value);
    var linked = new ActivateEvent { Scope = _scope, OrderingKey = "family-7", PredecessorId = original, PredecessorType = "T" };

    tracker.Stamp(linked, TrackedGuid.New().Value);

    await Assert.That(linked.PredecessorId).IsEqualTo(original);
    await Assert.That(linked.PredecessorType).IsEqualTo("T");
  }

  /// <summary>An unkeyed collective, a hand-written one and a message that is no collective at all are left alone.</summary>
  [Test]
  public async Task Stamp_AnythingButAKeyedCollectiveBase_IsLeftAloneAsync() {
    var tracker = new CollectivePredecessorTracker();
    var unkeyed = new ActivateEvent { Scope = _scope };
    var handWritten = new HandWrittenCollective { Scope = _scope };

    tracker.Stamp(unkeyed, TrackedGuid.New().Value);
    tracker.Stamp(unkeyed, TrackedGuid.New().Value);
    tracker.Stamp(handWritten, TrackedGuid.New().Value);
    tracker.Stamp("not a collective", TrackedGuid.New().Value);
    tracker.Stamp(null, TrackedGuid.New().Value);

    await Assert.That(unkeyed.PredecessorId).IsNull();
    await Assert.That(((ICollectiveEvent)handWritten).PredecessorId).IsNull();
    await Assert.That(((ICollectiveEvent)handWritten).PredecessorType).IsNull();
  }

  /// <summary>Past its capacity the map starts over, so the next new key's collective goes unlinked rather than the map growing.</summary>
  [Test]
  public async Task Stamp_PastCapacity_StartsOverAsync() {
    var tracker = new CollectivePredecessorTracker(capacity: 1);
    tracker.Stamp(new ActivateEvent { Scope = _scope, OrderingKey = "a" }, TrackedGuid.New().Value);
    tracker.Stamp(new ActivateEvent { Scope = _scope, OrderingKey = "b" }, TrackedGuid.New().Value);
    var nextOnA = new ActivateEvent { Scope = _scope, OrderingKey = "a" };
    var nextOnB = new ActivateEvent { Scope = _scope, OrderingKey = "b" };

    tracker.Stamp(nextOnB, TrackedGuid.New().Value);
    tracker.Stamp(nextOnA, TrackedGuid.New().Value);

    await Assert.That(nextOnB.PredecessorId).IsNotNull()
      .Because("the key remembered after starting over still links");
    await Assert.That(nextOnA.PredecessorId).IsNull()
      .Because("the key forgotten when the map started over goes unlinked, and applies as a collective always did");
    await Assert.That(tracker.Capacity).IsEqualTo(1);
    await Assert.That(new CollectivePredecessorTracker().Capacity).IsEqualTo(CollectivePredecessorTracker.DEFAULT_CAPACITY);
    await Assert.That(() => new CollectivePredecessorTracker(0)).ThrowsExactly<ArgumentOutOfRangeException>();
  }

  /// <summary>
  /// An unlinked collective serializes exactly as before the link existed, and a collective from a publisher that
  /// sends no link reads back with none: the link is wire-compatible both ways.
  /// </summary>
  [Test]
  public async Task Serialization_WithoutALink_WritesNoLinkAndReadsNoneAsync() {
    var options = new JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
    var unlinked = new LinkProbe { Scope = _scope, OrderingKey = "k" };

    var json = JsonSerializer.Serialize(unlinked, options);
    var fromOldPublisher = JsonSerializer.Deserialize<LinkProbe>("""{"Scope":null,"OrderingKey":"k"}""", options)!;
    var linked = JsonSerializer.Deserialize<LinkProbe>(
      JsonSerializer.Serialize(unlinked with { PredecessorId = Guid.Empty, PredecessorType = "T" }, options), options)!;

    await Assert.That(json).DoesNotContain("Predecessor");
    await Assert.That(fromOldPublisher.PredecessorId).IsNull();
    await Assert.That(linked.PredecessorId).IsEqualTo(Guid.Empty);
    await Assert.That(linked.PredecessorType).IsEqualTo("T");
  }

  /// <summary>A collective serialized by reflection for the wire-compatibility check only.</summary>
  public sealed record LinkProbe : CollectiveEventBase;
}
