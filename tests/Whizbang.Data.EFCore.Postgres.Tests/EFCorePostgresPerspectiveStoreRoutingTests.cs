using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the perspective store hands to its upsert strategy.
/// </summary>
/// <remarks>
/// The store is a thin router: every overload lands on the same strategy, differing only in what
/// it fills in for the caller. That makes the defaults the whole substance — and the ones that
/// matter are the two a caller cannot see going wrong. Passing default metadata over a caller's
/// own would overwrite the event identity a perspective row is audited by, and a null scope
/// reaching the strategy as null rather than an empty scope would throw from inside the SQL
/// builder, where the cause is no longer visible.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs</code-under-test>
[Category("Shard1")]
public class EFCorePostgresPerspectiveStoreRoutingTests : EFCoreTestBase {

  public sealed class ProbeModel {
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
  }

  private sealed record UpsertCall(
    string TableName, Guid Id, PerspectiveMetadata Metadata, PerspectiveScope Scope,
    IDictionary<string, object?>? PhysicalFields, bool ForceUpdateScope);

  /// <summary>Records what the store routed, and never touches the database.</summary>
  private sealed class RecordingStrategy : IDbUpsertStrategy {
    public List<UpsertCall> Calls { get; } = [];

    public Task UpsertPerspectiveRowAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model,
        PerspectiveMetadata metadata, PerspectiveScope scope,
        CancellationToken cancellationToken = default) where TModel : class {
      Calls.Add(new UpsertCall(tableName, id, metadata, scope, null, false));
      return Task.CompletedTask;
    }

    public Task UpsertPerspectiveRowAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model,
        PerspectiveMetadata metadata, PerspectiveScope scope, bool forceUpdateScope,
        CancellationToken cancellationToken = default) where TModel : class {
      Calls.Add(new UpsertCall(tableName, id, metadata, scope, null, forceUpdateScope));
      return Task.CompletedTask;
    }

    public Task UpsertPerspectiveRowWithPhysicalFieldsAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model,
        PerspectiveMetadata metadata, PerspectiveScope scope,
        IDictionary<string, object?> physicalFieldValues,
        CancellationToken cancellationToken = default) where TModel : class {
      Calls.Add(new UpsertCall(tableName, id, metadata, scope, physicalFieldValues, false));
      return Task.CompletedTask;
    }

    public Task UpsertPerspectiveRowWithPhysicalFieldsAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model,
        PerspectiveMetadata metadata, PerspectiveScope scope,
        IDictionary<string, object?> physicalFieldValues, bool forceUpdateScope,
        CancellationToken cancellationToken = default) where TModel : class {
      Calls.Add(new UpsertCall(tableName, id, metadata, scope, physicalFieldValues, forceUpdateScope));
      return Task.CompletedTask;
    }
  }

  private const string TABLE = "wh_per_probe";

  private (EFCorePostgresPerspectiveStore<ProbeModel> Store, RecordingStrategy Strategy) _store(
      WorkCoordinationDbContext ctx) {
    var strategy = new RecordingStrategy();
    return (new EFCorePostgresPerspectiveStore<ProbeModel>(ctx, TABLE, strategy), strategy);
  }

  private static readonly PerspectiveMetadata _callerMetadata = new() {
    EventType = "Shop.OrderPlaced",
    EventId = "0199aaaa-0000-0000-0000-000000000001",
    Timestamp = DateTime.UtcNow,
  };

  // ============================================================
  // Physical-field overloads
  // ============================================================

  [Test]
  public async Task PhysicalFields_WithoutMetadata_SuppliesTheDefaultAsync() {
    await using var ctx = CreateDbContext();
    var (store, strategy) = _store(ctx);
    var fields = new Dictionary<string, object?> { ["price"] = 10m };

    await store.UpsertWithPhysicalFieldsAsync(
      Guid.CreateVersion7(), new ProbeModel(), fields,
      new PerspectiveScope(), forceUpdateScope: false);

    var call = strategy.Calls.Single();
    await Assert.That(call.Metadata).IsNotNull();
    await Assert.That(call.PhysicalFields).IsEqualTo(fields);
  }

  [Test]
  public async Task PhysicalFields_WithMetadata_PassesTheCallersOwnAsync() {
    // The overload exists so a caller can carry the real event identity onto the row. Passing
    // the store's default here would overwrite what the row is audited by, and nothing at the
    // call site would show it.
    await using var ctx = CreateDbContext();
    var (store, strategy) = _store(ctx);

    await store.UpsertWithPhysicalFieldsAsync(
      Guid.CreateVersion7(), new ProbeModel(), new Dictionary<string, object?>(),
      new PerspectiveScope(), forceUpdateScope: false, _callerMetadata);

    var call = strategy.Calls.Single();
    await Assert.That(call.Metadata.EventType).IsEqualTo("Shop.OrderPlaced")
      .Because("the caller supplied the event identity precisely so the row records it");
  }

  [Test]
  public async Task PhysicalFields_WithANullScope_SendsAnEmptyScopeNotNullAsync() {
    // An unscoped perspective passes null. Forwarding that would fault inside the SQL builder,
    // where the cause is no longer visible from the call site.
    await using var ctx = CreateDbContext();
    var (store, strategy) = _store(ctx);

    await store.UpsertWithPhysicalFieldsAsync(
      Guid.CreateVersion7(), new ProbeModel(), new Dictionary<string, object?>(),
      scope: null, forceUpdateScope: false);

    await Assert.That(strategy.Calls.Single().Scope).IsNotNull();
  }

  [Test]
  public async Task PhysicalFields_WithMetadataAndANullScope_StillSendsAnEmptyScopeAsync() {
    // Both defaults on the same call — the overload pair has to agree.
    await using var ctx = CreateDbContext();
    var (store, strategy) = _store(ctx);

    await store.UpsertWithPhysicalFieldsAsync(
      Guid.CreateVersion7(), new ProbeModel(), new Dictionary<string, object?>(),
      scope: null, forceUpdateScope: false, _callerMetadata);

    var call = strategy.Calls.Single();
    await Assert.That(call.Scope).IsNotNull();
    await Assert.That(call.Metadata.EventType).IsEqualTo("Shop.OrderPlaced");
  }

  [Test]
  public async Task PhysicalFields_ForwardsForceUpdateScopeAsync() {
    // The flag decides whether an existing row's scope is overwritten. Dropping it silently
    // would leave a re-scoped row carrying its old tenant.
    await using var ctx = CreateDbContext();
    var (store, strategy) = _store(ctx);

    await store.UpsertWithPhysicalFieldsAsync(
      Guid.CreateVersion7(), new ProbeModel(), new Dictionary<string, object?>(),
      new PerspectiveScope(), forceUpdateScope: true);

    await Assert.That(strategy.Calls.Single().ForceUpdateScope).IsTrue();
  }

  // ============================================================
  // Partition-key routing
  // ============================================================

  [Test]
  public async Task APartitionKeyThatIsAlreadyAGuid_IsUsedAsTheRowIdAsync() {
    // Hashing a Guid would produce a different id than every other path uses for the same
    // partition, and the row would never be found again.
    await using var ctx = CreateDbContext();
    var (store, strategy) = _store(ctx);
    var key = Guid.CreateVersion7();

    await store.UpsertByPartitionKeyAsync(key, new ProbeModel(), new PerspectiveScope(),
      forceUpdateScope: false);

    await Assert.That(strategy.Calls.Single().Id).IsEqualTo(key);
  }

  [Test]
  public async Task AStringPartitionKey_HashesToAStableIdAsync() {
    // The id has to be derived deterministically or the same partition writes a new row on
    // every upsert.
    await using var ctx = CreateDbContext();
    var (store, strategy) = _store(ctx);

    await store.UpsertByPartitionKeyAsync("tenant-a", new ProbeModel(), new PerspectiveScope(),
      forceUpdateScope: false);
    await store.UpsertByPartitionKeyAsync("tenant-a", new ProbeModel(), new PerspectiveScope(),
      forceUpdateScope: false);

    await Assert.That(strategy.Calls[0].Id).IsEqualTo(strategy.Calls[1].Id)
      .Because("a non-deterministic id writes a new row for the same partition every time");
  }

  [Test]
  public async Task DifferentStringPartitionKeys_GetDifferentIdsAsync() {
    await using var ctx = CreateDbContext();
    var (store, strategy) = _store(ctx);

    await store.UpsertByPartitionKeyAsync("tenant-a", new ProbeModel(), new PerspectiveScope(),
      forceUpdateScope: false);
    await store.UpsertByPartitionKeyAsync("tenant-b", new ProbeModel(), new PerspectiveScope(),
      forceUpdateScope: false);

    await Assert.That(strategy.Calls[0].Id).IsNotEqualTo(strategy.Calls[1].Id)
      .Because("two partitions colliding on one id would merge their rows");
  }

  [Test]
  public async Task EveryRouteTargetsTheStoresTableAsync() {
    // The table name is fixed at construction; a route that derived its own would write a
    // perspective's rows somewhere nothing reads.
    await using var ctx = CreateDbContext();
    var (store, strategy) = _store(ctx);

    await store.UpsertByPartitionKeyAsync("tenant-a", new ProbeModel(), new PerspectiveScope(),
      forceUpdateScope: false);
    await store.UpsertWithPhysicalFieldsAsync(
      Guid.CreateVersion7(), new ProbeModel(), new Dictionary<string, object?>(),
      new PerspectiveScope(), forceUpdateScope: false);

    await Assert.That(strategy.Calls.All(c => c.TableName == TABLE)).IsTrue();
  }
}
