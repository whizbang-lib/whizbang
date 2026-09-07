using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Coverage for <see cref="DapperPostgresPerspectiveStore{TModel}"/>'s <c>GetByPartitionKeyAsync</c>
/// — the read half of the partition-key API pair that
/// <see cref="DapperPostgresPerspectiveStoreTests"/> never exercises (it only round-trips through
/// stream ids). Reuses <see cref="DapperPostgresPerspectiveStoreTests.TestModel"/> and its
/// generated <see cref="DapperPerspectiveTestJsonContext"/> directly — both are internal/public to
/// this test project already, no need to redeclare them.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperPostgresPerspectiveStore.cs</code-under-test>
[NotInParallel("PostgreSQL")]
public class DapperPostgresPerspectiveStoreCoverageTests : PostgresTestBase {
  private const string TABLE_NAME = "wh_per_dapper_coverage_test";
  private JsonSerializerOptions _jsonOptions = null!;

  [Before(Test)]
  public async Task CreatePerspectiveTableAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var createSql = $"CREATE TABLE IF NOT EXISTS {TABLE_NAME} (" +
        "id UUID PRIMARY KEY, " +
        "data JSONB NOT NULL, " +
        "metadata JSONB NOT NULL DEFAULT '{}'::jsonb, " +
        "scope JSONB NOT NULL DEFAULT '{}'::jsonb, " +
        "created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), " +
        "updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), " +
        "version INT NOT NULL DEFAULT 1)";
    await using var cmd = new NpgsqlCommand(createSql, conn);
    await cmd.ExecuteNonQueryAsync();

    _jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        DapperPerspectiveTestJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
  }

  // GetByPartitionKeyAsync is the read side of a pair -- UpsertByPartitionKeyAsync writes under
  // the same MD5-derived row id. If the delegation to GetByStreamIdAsync ever used a different id
  // derivation, or dropped the partition key en route, every read-model lookup keyed by a
  // non-Guid partition key (a tenant slug, an external id) would silently come back empty even
  // though the row a prior upsert wrote is sitting right there under a different row id.
  [Test]
  public async Task GetByPartitionKeyAsync_StringKey_ReturnsTheModelUpsertedUnderTheSameKeyAsync() {
    var store = new DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>(
      ConnectionString, TABLE_NAME, _jsonOptions);
    const string partitionKey = "tenant-partition-key";
    await store.UpsertByPartitionKeyAsync(
      partitionKey, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "partition-lookup" });

    var result = await store.GetByPartitionKeyAsync(partitionKey);

    await Assert.That(result).IsNotNull()
      .Because("a partition-key upsert followed by a partition-key read of the SAME key must find the row -- both must derive the same row id from it");
    await Assert.That(result!.Name).IsEqualTo("partition-lookup");
  }

  [Test]
  public async Task GetByPartitionKeyAsync_UnknownKey_ReturnsNullAsync() {
    var store = new DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>(
      ConnectionString, TABLE_NAME, _jsonOptions);

    var result = await store.GetByPartitionKeyAsync("no-such-partition-key");

    await Assert.That(result).IsNull()
      .Because("a partition key nothing was ever upserted under must report absent, not fabricate a row");
  }

  // ── UpsertWithPhysicalFieldsAsync: the Dapper store deliberately ignores the physical-field
  // dictionary (it has no physical columns to materialize into) but must still perform the same
  // JSONB upsert as the plain overloads. If either overload ever started throwing on, or writing,
  // the physical fields, a perspective declared with physical fields would stop persisting entirely
  // on a Dapper-backed store while the EF Core store kept working — a provider-only data loss.

  [Test]
  public async Task UpsertWithPhysicalFieldsAsync_IgnoresPhysicalFields_StillWritesModelAndScopeAsync() {
    var store = new DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>(
      ConnectionString, TABLE_NAME, _jsonOptions);
    var id = Guid.CreateVersion7();
    var physicalFields = new Dictionary<string, object?> { ["not_a_column"] = "ignored" };

    await store.UpsertWithPhysicalFieldsAsync(
      id,
      new DapperPostgresPerspectiveStoreTests.TestModel { Name = "physical-insert" },
      physicalFields,
      new PerspectiveScope { TenantId = "tenant-physical" });

    var (name, tenant) = await _readNameAndTenantAsync(id);
    await Assert.That(name).IsEqualTo("physical-insert")
      .Because("the physical-field overload must still persist the model JSON; the dictionary is ignored, not the write");
    await Assert.That(tenant).IsEqualTo("tenant-physical")
      .Because("scope is written on INSERT even through the physical-field overload");
  }

  [Test]
  public async Task UpsertWithPhysicalFieldsAsync_DefaultOverload_DoesNotRewriteScopeOnUpdateAsync() {
    var store = new DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>(
      ConnectionString, TABLE_NAME, _jsonOptions);
    var id = Guid.CreateVersion7();
    var physicalFields = new Dictionary<string, object?>();

    await store.UpsertWithPhysicalFieldsAsync(
      id, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "first" }, physicalFields,
      new PerspectiveScope { TenantId = "owner-tenant" });
    await store.UpsertWithPhysicalFieldsAsync(
      id, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "second" }, physicalFields,
      new PerspectiveScope { TenantId = "intruder-tenant" });

    var (name, tenant) = await _readNameAndTenantAsync(id);
    await Assert.That(name).IsEqualTo("second")
      .Because("the second upsert must land — ON CONFLICT updates the data column");
    await Assert.That(tenant).IsEqualTo("owner-tenant")
      .Because("the four-argument overload passes forceUpdateScope: false, so a later event carrying a different tenant must NOT be able to reassign an existing row's owner");
  }

  [Test]
  public async Task UpsertWithPhysicalFieldsAsync_ForceUpdateScope_RewritesScopeOnUpdateAsync() {
    var store = new DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>(
      ConnectionString, TABLE_NAME, _jsonOptions);
    var id = Guid.CreateVersion7();
    var physicalFields = new Dictionary<string, object?>();

    await store.UpsertWithPhysicalFieldsAsync(
      id, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "first" }, physicalFields,
      new PerspectiveScope { TenantId = "original-tenant" }, forceUpdateScope: false);
    await store.UpsertWithPhysicalFieldsAsync(
      id, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "second" }, physicalFields,
      new PerspectiveScope { TenantId = "reassigned-tenant" }, forceUpdateScope: true);

    var (_, tenant) = await _readNameAndTenantAsync(id);
    await Assert.That(tenant).IsEqualTo("reassigned-tenant")
      .Because("forceUpdateScope: true is the only way an explicit scope-change event can move a row to a new tenant; without it the row would be stranded under its original owner forever");
  }

  // ── UpsertByPartitionKeyAsync scope overloads ─────────────────────────────
  // Same forceUpdateScope contract as the stream-id overloads, reached through the partition-key
  // entry point. A regression here silently either strands rows under a stale scope or lets any
  // subsequent event reassign ownership — both are tenant-isolation faults, not cosmetic ones.

  [Test]
  public async Task UpsertByPartitionKeyAsync_WithScope_DoesNotRewriteScopeOnUpdateAsync() {
    var store = new DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>(
      ConnectionString, TABLE_NAME, _jsonOptions);
    const string partitionKey = "pk-scope-preserved";

    await store.UpsertByPartitionKeyAsync(
      partitionKey, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "first" },
      new PerspectiveScope { TenantId = "owner-tenant" });
    await store.UpsertByPartitionKeyAsync(
      partitionKey, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "second" },
      new PerspectiveScope { TenantId = "intruder-tenant" });

    var (name, tenant) = await _readNameAndTenantAsync(_partitionKeyRowId(partitionKey));
    await Assert.That(name).IsEqualTo("second")
      .Because("both upserts must address the same row id derived from the same partition key");
    await Assert.That(tenant).IsEqualTo("owner-tenant")
      .Because("the scope overload without forceUpdateScope must leave an existing row's tenant untouched");
  }

  [Test]
  public async Task UpsertByPartitionKeyAsync_WithForceUpdateScope_RewritesScopeOnUpdateAsync() {
    var store = new DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>(
      ConnectionString, TABLE_NAME, _jsonOptions);
    const string partitionKey = "pk-scope-forced";

    await store.UpsertByPartitionKeyAsync(
      partitionKey, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "first" },
      new PerspectiveScope { TenantId = "original-tenant" }, forceUpdateScope: false);
    await store.UpsertByPartitionKeyAsync(
      partitionKey, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "second" },
      new PerspectiveScope { TenantId = "reassigned-tenant" }, forceUpdateScope: true);

    var (_, tenant) = await _readNameAndTenantAsync(_partitionKeyRowId(partitionKey));
    await Assert.That(tenant).IsEqualTo("reassigned-tenant")
      .Because("only the forceUpdateScope: true partition-key overload can reassign an existing row's scope");
  }

  // ── PurgeByPartitionKeyAsync ─────────────────────────────────────────────
  // Delete keyed by the same derivation the write side uses. If purge derived the row id
  // differently it would delete nothing (leaving a soft-deleted read model visible forever) or,
  // worse, delete some other partition's row.

  [Test]
  public async Task PurgeByPartitionKeyAsync_RemovesTheRowWrittenUnderTheSameKeyAsync() {
    var store = new DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>(
      ConnectionString, TABLE_NAME, _jsonOptions);
    const string doomedKey = "pk-to-purge";
    const string survivorKey = "pk-to-keep";
    await store.UpsertByPartitionKeyAsync(
      doomedKey, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "doomed" });
    await store.UpsertByPartitionKeyAsync(
      survivorKey, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "survivor" });

    await store.PurgeByPartitionKeyAsync(doomedKey);

    await Assert.That(await store.GetByPartitionKeyAsync(doomedKey)).IsNull()
      .Because("purge must resolve the same row id the write side derived from that partition key, or the row survives the delete");
    await Assert.That((await store.GetByPartitionKeyAsync(survivorKey))?.Name).IsEqualTo("survivor")
      .Because("purging one partition key must not touch another key's row");
  }

  // ── Guid partition keys map to themselves, not to an MD5 of their text ────
  // A Guid partition key IS the row id. Hashing it instead would put the partition-key API and the
  // stream-id API on two different rows for the same aggregate, so a perspective written through
  // one and read through the other would come back empty.

  [Test]
  public async Task UpsertByPartitionKeyAsync_GuidKey_WritesTheRowAddressedByThatSameGuidStreamIdAsync() {
    var store = new DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>(
      ConnectionString, TABLE_NAME, _jsonOptions);
    var key = Guid.CreateVersion7();

    await store.UpsertByPartitionKeyAsync(
      key, new DapperPostgresPerspectiveStoreTests.TestModel { Name = "guid-key-identity" });

    var byStreamId = await store.GetByStreamIdAsync(key);
    await Assert.That(byStreamId).IsNotNull()
      .Because("a Guid partition key is used verbatim as the row id — MD5-hashing its text would strand the row under an id no stream-id read can ever find");
    await Assert.That(byStreamId!.Name).IsEqualTo("guid-key-identity");
    await Assert.That(_partitionKeyRowId(key.ToString())).IsNotEqualTo(key)
      .Because("the hashed derivation of the same Guid's text is a different id, so the read above can only be satisfied by the identity arm");
  }

  // ── FlushAsync is a genuine no-op for this store ──────────────────────────
  // Dapper commits each upsert immediately, so there is nothing buffered to flush. Callers await
  // FlushAsync on the perspective-apply hot path; if it ever started opening a connection it would
  // add a round trip per applied event and would fault whenever the store was constructed with a
  // connection string it is not expected to dial (fan-out registration, disposed scopes).

  [Test]
  public async Task FlushAsync_DoesNotTouchTheDatabase_CompletesEvenWithAnUnreachableConnectionStringAsync() {
    var store = new DapperPostgresPerspectiveStore<DapperPostgresPerspectiveStoreTests.TestModel>(
      "Host=127.0.0.1;Port=1;Database=unreachable;Username=u;Password=p;Timeout=1",
      TABLE_NAME,
      _jsonOptions);

    var flush = store.FlushAsync();

    await Assert.That(flush.IsCompletedSuccessfully).IsTrue()
      .Because("a store that commits on every write has nothing to flush — the task must already be complete, never a pending round trip");
    await flush;
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
    Justification = "Mirrors the store's own deterministic partition-key-to-row-id mapping so the test can address the row directly; identity derivation, not a security control.")]
  private static Guid _partitionKeyRowId(string partitionKey) =>
    new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(partitionKey)));

  private async Task<(string? Name, string? TenantId)> _readNameAndTenantAsync(Guid id) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      $"SELECT data->>'Name', scope->>'t' FROM {TABLE_NAME} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return (null, null);
    }
    return (
      reader.IsDBNull(0) ? null : reader.GetString(0),
      reader.IsDBNull(1) ? null : reader.GetString(1));
  }
}
