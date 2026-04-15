using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Npgsql;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Integration tests for <see cref="DapperPostgresPerspectiveStore{TModel}"/>.
/// Validates scope handling: set on INSERT, excluded from UPDATE, force-updated via IScopeEvent path.
/// </summary>
[NotInParallel("DapperPerspectiveStoreTests")]
public class DapperPostgresPerspectiveStoreTests : PostgresTestBase {
  private const string TABLE_NAME = "wh_per_dapper_test";
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

  [Test]
  public async Task DapperUpsert_Insert_SetsScopeAsync() {
    // Arrange
    var store = new DapperPostgresPerspectiveStore<TestModel>(ConnectionString, TABLE_NAME, _jsonOptions);
    var id = Guid.CreateVersion7();
    var scope = new PerspectiveScope { TenantId = "tenant-dapper", UserId = "user-1" };

    // Act
    await store.UpsertAsync(id, new TestModel { Name = "test" }, scope);

    // Assert
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        $"SELECT scope->>'t' as tenant, scope->>'u' as user_id FROM {TABLE_NAME} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();

    var tenant = reader.GetString(0);
    var userId = reader.GetString(1);
    await Assert.That(tenant).IsEqualTo("tenant-dapper");
    await Assert.That(userId).IsEqualTo("user-1");
  }

  [Test]
  public async Task DapperUpsert_Update_ExcludesScopeAsync() {
    // Arrange
    var store = new DapperPostgresPerspectiveStore<TestModel>(ConnectionString, TABLE_NAME, _jsonOptions);
    var id = Guid.CreateVersion7();
    var scopeA = new PerspectiveScope { TenantId = "tenant-A" };
    var scopeB = new PerspectiveScope { TenantId = "tenant-B" };

    // INSERT with scope A
    await store.UpsertAsync(id, new TestModel { Name = "v1" }, scopeA);

    // Act - UPDATE with scope B (should be ignored)
    await store.UpsertAsync(id, new TestModel { Name = "v2" }, scopeB);

    // Assert - scope is still A
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        $"SELECT scope->>'t' FROM {TABLE_NAME} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    var tenant = (string)(await cmd.ExecuteScalarAsync())!;

    await Assert.That(tenant).IsEqualTo("tenant-A");
  }

  [Test]
  public async Task DapperUpsert_ForceScope_UpdatesScopeAsync() {
    // Arrange
    var store = new DapperPostgresPerspectiveStore<TestModel>(ConnectionString, TABLE_NAME, _jsonOptions);
    var id = Guid.CreateVersion7();
    var scopeA = new PerspectiveScope { TenantId = "tenant-old" };
    var scopeB = new PerspectiveScope { TenantId = "tenant-new", OrganizationId = "org-new" };

    // INSERT with scope A
    await store.UpsertAsync(id, new TestModel { Name = "v1" }, scopeA);

    // Act - UPDATE with forceUpdateScope = true (IScopeEvent path)
    await store.UpsertAsync(id, new TestModel { Name = "v2" }, scopeB, forceUpdateScope: true);

    // Assert - scope is now B
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        $"SELECT scope->>'t' as tenant, scope->>'o' as org FROM {TABLE_NAME} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();

    await Assert.That(reader.GetString(0)).IsEqualTo("tenant-new");
    await Assert.That(reader.GetString(1)).IsEqualTo("org-new");
  }

  [Test]
  public async Task DapperUpsert_MultipleUpdates_PreservesScopeAsync() {
    // Arrange
    var store = new DapperPostgresPerspectiveStore<TestModel>(ConnectionString, TABLE_NAME, _jsonOptions);
    var id = Guid.CreateVersion7();
    var originalScope = new PerspectiveScope { TenantId = "tenant-original" };

    await store.UpsertAsync(id, new TestModel { Name = "v0" }, originalScope);

    // Act - 5 updates with different scopes (none forced)
    for (var i = 1; i <= 5; i++) {
      var differentScope = new PerspectiveScope { TenantId = $"tenant-{i}" };
      await store.UpsertAsync(id, new TestModel { Name = $"v{i}" }, differentScope);
    }

    // Assert - scope still original, version is 6
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        $"SELECT scope->>'t', version FROM {TABLE_NAME} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();

    await Assert.That(reader.GetString(0)).IsEqualTo("tenant-original");
    await Assert.That(reader.GetInt32(1)).IsEqualTo(6);
  }

  [Test]
  public async Task DapperUpsert_GetByStreamId_ReturnsModelAsync() {
    // Arrange
    var store = new DapperPostgresPerspectiveStore<TestModel>(ConnectionString, TABLE_NAME, _jsonOptions);
    var id = Guid.CreateVersion7();
    await store.UpsertAsync(id, new TestModel { Name = "lookup-test" });

    // Act
    var result = await store.GetByStreamIdAsync(id);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("lookup-test");
  }

  [Test]
  public async Task DapperUpsert_GetByStreamId_WhenNotFound_ReturnsNullAsync() {
    // Arrange
    var store = new DapperPostgresPerspectiveStore<TestModel>(ConnectionString, TABLE_NAME, _jsonOptions);

    // Act
    var result = await store.GetByStreamIdAsync(Guid.CreateVersion7());

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task DapperUpsert_PurgeAsync_RemovesRowAsync() {
    // Arrange
    var store = new DapperPostgresPerspectiveStore<TestModel>(ConnectionString, TABLE_NAME, _jsonOptions);
    var id = Guid.CreateVersion7();
    await store.UpsertAsync(id, new TestModel { Name = "purge-me" });

    // Act
    await store.PurgeAsync(id);

    // Assert
    var result = await store.GetByStreamIdAsync(id);
    await Assert.That(result).IsNull();
  }

  internal sealed class TestModel {
    public string Name { get; set; } = "";
  }
}

[JsonSerializable(typeof(DapperPostgresPerspectiveStoreTests.TestModel))]
[JsonSerializable(typeof(PerspectiveScope))]
internal sealed partial class DapperPerspectiveTestJsonContext : JsonSerializerContext;
