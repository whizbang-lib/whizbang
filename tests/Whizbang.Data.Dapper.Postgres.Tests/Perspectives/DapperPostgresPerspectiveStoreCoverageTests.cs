using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
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
}
