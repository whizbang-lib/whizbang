using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Dispatcher;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The publisher's half of the predecessor link on a keyed collective (#1003): which messages are marked for a link,
/// and the payload field names the store writes the link under (migration 190, <c>{p,predecessorId}</c> and
/// <c>{p,predecessorType}</c>), which must be the names the payload serializer reads.
/// </summary>
/// <docs>fundamentals/messaging/collective-events#ordering-across-services</docs>
[Category("CollectiveEvents")]
public class CollectivePredecessorLinkTests {
  private sealed record ActivateEvent : CollectiveEventBase;

  private sealed record HandWrittenCollective : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  private static readonly TenantCollectiveScope _scope = new("t-1");

  [Test]
  public async Task TypeFor_AKeyedCollective_IsItsEventStoreTypeNameAsync() {
    var keyed = new ActivateEvent { Scope = _scope, OrderingKey = "family-7" };

    await Assert.That(CollectivePredecessorLink.TypeFor(keyed)).IsEqualTo(TypeNameFormatter.Format(typeof(ActivateEvent)));
  }

  /// <summary>An unkeyed or blank-keyed collective, a hand-written one, and anything that is no collective get no link.</summary>
  [Test]
  public async Task TypeFor_AnythingButAKeyedCollectiveBase_IsNullAsync() {
    await Assert.That(CollectivePredecessorLink.TypeFor(new ActivateEvent { Scope = _scope })).IsNull();
    await Assert.That(CollectivePredecessorLink.TypeFor(new ActivateEvent { Scope = _scope, OrderingKey = " " })).IsNull();
    await Assert.That(CollectivePredecessorLink.TypeFor(new HandWrittenCollective { Scope = _scope })).IsNull();
    await Assert.That(CollectivePredecessorLink.TypeFor("not a collective")).IsNull();
    await Assert.That(CollectivePredecessorLink.TypeFor(null)).IsNull();
    await Assert.That(((ICollectiveEvent)new HandWrittenCollective { Scope = _scope }).PredecessorId).IsNull();
    await Assert.That(((ICollectiveEvent)new HandWrittenCollective { Scope = _scope }).PredecessorType).IsNull();
  }

  /// <summary>
  /// The payload serializer writes the link as <c>predecessorId</c> and <c>predecessorType</c>, the fields the store
  /// writes it into, leaves them out when there is no link, and reads them back: a collective from a publisher that
  /// sends no link reads with none, and one the store linked reads with its link.
  /// </summary>
  [Test]
  public async Task Serialization_TheLinkFields_HaveTheNamesTheStoreWritesAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = options.GetTypeInfo(typeof(DispatcherKeyedCollectiveStreamTests.KeyedFlipCollectiveEvent));
    var unlinked = new DispatcherKeyedCollectiveStreamTests.KeyedFlipCollectiveEvent { Scope = _scope, OrderingKey = "k" };
    var predecessorId = Guid.CreateVersion7();

    var unlinkedJson = JsonSerializer.Serialize(unlinked, typeInfo);
    var stored = JsonSerializer.SerializeToNode(unlinked, typeInfo)!.AsObject();
    stored["predecessorId"] = predecessorId.ToString();
    stored["predecessorType"] = "Some.Collective";
    var linked = (DispatcherKeyedCollectiveStreamTests.KeyedFlipCollectiveEvent)stored.Deserialize(typeInfo)!;
    var fromOldPublisher = (DispatcherKeyedCollectiveStreamTests.KeyedFlipCollectiveEvent)JsonSerializer.Deserialize(unlinkedJson, typeInfo)!;

    await Assert.That(unlinkedJson).DoesNotContain("redecessor");
    await Assert.That(linked.PredecessorId).IsEqualTo(predecessorId);
    await Assert.That(linked.PredecessorType).IsEqualTo("Some.Collective");
    await Assert.That(fromOldPublisher.PredecessorId).IsNull();
  }
}
