using System.Collections.Concurrent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Pins the default-interface-method delegation on <see cref="IPerspectiveStore{TModel}"/>.
/// Custom implementations (test doubles, third-party stores like Marten) only need to
/// override the "shortest" overloads; the richer overloads — with scope, forceUpdateScope,
/// metadata, physical fields — fall through to the canonical short form via default impls.
///
/// If a future refactor reorders parameters in the default-impl chain, every existing
/// test fake silently drops scope / metadata / physical fields. These tests lock the
/// fan-in: a minimal fake records ONLY the short overloads; calling the rich overloads
/// must still land on the short ones, in the same order, with the same values.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives</docs>
public class IPerspectiveStoreDefaultsTests {

  [Test]
  public async Task GetMetadataByStreamIdAsync_DefaultReturnsNullAsync() {
    // Custom stores that don't override the metadata accessor return null so
    // the runner falls back to "apply all events" — locks against a future
    // accidental break of that compatibility contract.
    IPerspectiveStore<_Model> store = new _ShortOverloadStore();
    var result = await store.GetMetadataByStreamIdAsync(Guid.NewGuid());

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task UpsertAsync_WithScope_DelegatesToShortFormAsync() {
    var store = new _ShortOverloadStore();
    var streamId = Guid.NewGuid();
    var model = new _Model { Name = "x" };
    var scope = new PerspectiveScope { TenantId = "t1" };

    await ((IPerspectiveStore<_Model>)store).UpsertAsync(streamId, model, scope);

    await Assert.That(store.UpsertCalls).IsEqualTo(1);
    await Assert.That(store.LastStreamId).IsEqualTo(streamId);
    await Assert.That(store.LastModel).IsSameReferenceAs(model);
  }

  [Test]
  public async Task UpsertAsync_WithScopeAndForceUpdate_DelegatesToShortFormAsync() {
    var store = new _ShortOverloadStore();
    var streamId = Guid.NewGuid();
    var model = new _Model { Name = "x" };
    var scope = new PerspectiveScope { TenantId = "t1" };

    await ((IPerspectiveStore<_Model>)store).UpsertAsync(streamId, model, scope, forceUpdateScope: true);

    await Assert.That(store.UpsertCalls).IsEqualTo(1);
  }

  [Test]
  public async Task UpsertAsync_WithMetadata_DropsMetadataAndDelegatesAsync() {
    // The richest overload (5 params + metadata) falls through to the
    // forceUpdateScope overload — which falls through to the scope overload —
    // which falls through to the short form. End-to-end: one short call.
    var store = new _ShortOverloadStore();
    var metadata = new PerspectiveMetadata { EventId = Guid.NewGuid().ToString(), EventType = "TestEvent" };

    await ((IPerspectiveStore<_Model>)store).UpsertAsync(
      Guid.NewGuid(),
      new _Model(),
      new PerspectiveScope(),
      forceUpdateScope: false,
      metadata,
      CancellationToken.None);

    await Assert.That(store.UpsertCalls).IsEqualTo(1);
  }

  [Test]
  public async Task UpsertWithPhysicalFieldsAsync_WithForceUpdate_DelegatesAsync() {
    var store = new _ShortOverloadStore();
    var physical = new ConcurrentDictionary<string, object?> { ["col"] = 1 };

    await ((IPerspectiveStore<_Model>)store).UpsertWithPhysicalFieldsAsync(
      Guid.NewGuid(),
      new _Model(),
      physical,
      scope: null,
      forceUpdateScope: true);

    await Assert.That(store.UpsertPhysicalCalls).IsEqualTo(1);
  }

  [Test]
  public async Task UpsertWithPhysicalFieldsAsync_WithMetadata_DropsAndDelegatesAsync() {
    var store = new _ShortOverloadStore();

    await ((IPerspectiveStore<_Model>)store).UpsertWithPhysicalFieldsAsync(
      Guid.NewGuid(),
      new _Model(),
      new Dictionary<string, object?>(),
      scope: null,
      forceUpdateScope: false,
      metadata: new PerspectiveMetadata { EventId = Guid.NewGuid().ToString(), EventType = "T" },
      CancellationToken.None);

    await Assert.That(store.UpsertPhysicalCalls).IsEqualTo(1);
  }

  [Test]
  public async Task UpsertByPartitionKeyAsync_WithScope_DelegatesToShortFormAsync() {
    var store = new _ShortOverloadStore();

    await ((IPerspectiveStore<_Model>)store).UpsertByPartitionKeyAsync<string>("k", new _Model(), new PerspectiveScope());

    await Assert.That(store.UpsertByPartitionCalls).IsEqualTo(1);
  }

  [Test]
  public async Task UpsertByPartitionKeyAsync_WithForceUpdate_DelegatesAsync() {
    var store = new _ShortOverloadStore();

    await ((IPerspectiveStore<_Model>)store).UpsertByPartitionKeyAsync<string>(
      "k", new _Model(), new PerspectiveScope(), forceUpdateScope: true);

    await Assert.That(store.UpsertByPartitionCalls).IsEqualTo(1);
  }

  private sealed class _Model {
    public string Name { get; init; } = "";
  }

  private sealed class _ShortOverloadStore : IPerspectiveStore<_Model> {
    public int UpsertCalls { get; private set; }
    public int UpsertPhysicalCalls { get; private set; }
    public int UpsertByPartitionCalls { get; private set; }
    public Guid? LastStreamId { get; private set; }
    public _Model? LastModel { get; private set; }

    public Task<_Model?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default)
      => Task.FromResult<_Model?>(null);

    public Task UpsertAsync(Guid streamId, _Model model, CancellationToken cancellationToken = default) {
      UpsertCalls++;
      LastStreamId = streamId;
      LastModel = model;
      return Task.CompletedTask;
    }

    public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId,
      _Model model,
      IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope = null,
      CancellationToken cancellationToken = default) {
      UpsertPhysicalCalls++;
      return Task.CompletedTask;
    }

    public Task<_Model?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.FromResult<_Model?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, _Model model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull {
      UpsertByPartitionCalls++;
      return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;
  }
}
