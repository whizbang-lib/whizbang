#pragma warning disable CA1707

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Unit coverage for the collective scope base (polymorphic serialization), the
/// <see cref="CollectiveEventBase"/> authoring base, and the <see cref="CollectiveRouting"/> sink constant.
/// </summary>
public class CollectiveScopeBaseRoutingTests {

  private sealed record _archiveEvent : CollectiveEventBase {
    public string Note { get; init; } = "";
  }

  [Test]
  public async Task CollectiveEventBase_IsAnEvent_CarriesGeneratedStreamAndScopeAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    var evt = new _archiveEvent { StreamId = streamId, Scope = new TenantCollectiveScope("t-1"), Note = "n" };

    await Assert.That(evt is IEvent).IsTrue();
    await Assert.That(evt is ICollectiveEvent).IsTrue();
    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(((TenantCollectiveScope)evt.Scope).TenantId).IsEqualTo("t-1");
  }

  [Test]
  public async Task CollectiveScope_RoundTripsPolymorphically_ViaScopeKindDiscriminatorAsync() {
    CollectiveScope scope = new TenantCollectiveScope("tenant-42");
    var json = JsonSerializer.Serialize(scope);

    await Assert.That(json).Contains("$scopeKind");
    await Assert.That(json).Contains("tenant");

    var roundTripped = JsonSerializer.Deserialize<CollectiveScope>(json);
    await Assert.That(roundTripped).IsTypeOf<TenantCollectiveScope>();
    await Assert.That(roundTripped!.ScopeKind).IsEqualTo("tenant");
    await Assert.That(((TenantCollectiveScope)roundTripped).TenantId).IsEqualTo("tenant-42");
  }
}
