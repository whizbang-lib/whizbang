using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

// The Rename/ExecuteAsync apply path is [Obsolete] (superseded by the ledger-aware reconcile), but it remains
// functional and these tests deliberately exercise it to lock in that legacy behavior until it is removed.
#pragma warning disable CS0618

/// <summary>
/// Integration tests for DapperEventTypeRenameTool.
/// Verifies drift detection and the cross-table UPDATE executed in a single transaction.
/// </summary>
[Category("Integration")]
public class DapperEventTypeRenameToolTests : IAsyncDisposable {
  private string? _testDatabaseName;
  private string? _connectionString;
  private PostgresConnectionFactory? _connectionFactory;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"test_{Guid.NewGuid():N}";
    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    };
    _connectionString = builder.ConnectionString;
    _connectionFactory = new PostgresConnectionFactory(_connectionString);

    var initializer = new PostgresSchemaInitializer(_connectionString);
    await initializer.InitializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();
        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}'
          AND pid <> pg_backend_pid()");
        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch { /* ignore */ }
      _testDatabaseName = null;
      _connectionString = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  [Test]
  public async Task DetectRenames_NoDrift_ReturnsEmptyAsync() {
    const string clr = "Old.Namespace.FooEvent, TestApp";
    const string pinnedId = "11111111-1111-1111-1111-111111111111";

    await _seedRegistryAsync(clr, pinnedId);

    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SampleType), clr, "event", pinnedId)
    ]);
    var tool = new DapperEventTypeRenameTool(catalog, _connectionFactory!);

    var detected = await tool.DetectRenamesAsync();

    await Assert.That(detected).IsEmpty();
  }

  [Test]
  public async Task DetectRenames_PinnedDrift_ReturnsPendingRenameAsync() {
    const string oldClr = "Old.Namespace.FooEvent, TestApp";
    const string newClr = "New.Namespace.FooEvent, TestApp";
    const string pinnedId = "11111111-1111-1111-1111-111111111111";

    await _seedRegistryAsync(oldClr, pinnedId);

    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SampleType), newClr, "event", pinnedId)
    ]);
    var tool = new DapperEventTypeRenameTool(catalog, _connectionFactory!);

    var detected = await tool.DetectRenamesAsync();

    await Assert.That(detected).Count().IsEqualTo(1);
    await Assert.That(detected[0].PinnedId).IsEqualTo(pinnedId);
    await Assert.That(detected[0].OldClrTypeName).IsEqualTo(oldClr);
    await Assert.That(detected[0].NewClrTypeName).IsEqualTo(newClr);
  }

  [Test]
  public async Task ExecuteAsync_RewritesAllSixTablesAsync() {
    const string oldClr = "Old.Namespace.FooEvent, TestApp";
    const string newClr = "New.Namespace.FooEvent, TestApp";
    const string pinnedId = "11111111-1111-1111-1111-111111111111";

    await _seedRegistryAsync(oldClr, pinnedId);
    await _seedEventStoreAsync(oldClr);
    await _seedInboxAsync(oldClr);
    await _seedOutboxAsync(oldClr);
    await _seedMessageAssociationsAsync(oldClr);

    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SampleType), newClr, "event", pinnedId)
    ]);
    var tool = new DapperEventTypeRenameTool(catalog, _connectionFactory!);

    await tool.ExecuteAsync();

    var eventRows = await _countAsync("wh_event_store", "event_type", newClr);
    var aggregateRows = await _countAsync("wh_event_store", "aggregate_type", newClr);
    var inboxRows = await _countAsync("wh_inbox", "message_type", newClr);
    var outboxMessageRows = await _countAsync("wh_outbox", "message_type", newClr);
    var outboxEnvelopeRows = await _countAsync("wh_outbox", "envelope_type", newClr);
    var associationRows = await _countAsync("wh_message_associations", "message_type", newClr);
    var registryRows = await _countAsync("wh_message_type_registry", "clr_type_name", newClr);

    await Assert.That(eventRows).IsEqualTo(1);
    await Assert.That(aggregateRows).IsEqualTo(1);
    await Assert.That(inboxRows).IsEqualTo(1);
    await Assert.That(outboxMessageRows).IsEqualTo(1);
    await Assert.That(outboxEnvelopeRows).IsEqualTo(1);
    await Assert.That(associationRows).IsEqualTo(1);
    await Assert.That(registryRows).IsEqualTo(1);

    // No rows remain with the old name
    var residualEventRows = await _countAsync("wh_event_store", "event_type", oldClr);
    await Assert.That(residualEventRows).IsEqualTo(0);
  }

  [Test]
  public async Task ExecuteAsync_IsIdempotentAsync() {
    const string oldClr = "Old.Namespace.FooEvent, TestApp";
    const string newClr = "New.Namespace.FooEvent, TestApp";
    const string pinnedId = "11111111-1111-1111-1111-111111111111";

    await _seedRegistryAsync(oldClr, pinnedId);
    await _seedEventStoreAsync(oldClr);

    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SampleType), newClr, "event", pinnedId)
    ]);
    var tool = new DapperEventTypeRenameTool(catalog, _connectionFactory!);

    await tool.ExecuteAsync();
    await tool.ExecuteAsync();

    var afterTwoRuns = await _countAsync("wh_event_store", "event_type", newClr);
    await Assert.That(afterTwoRuns).IsEqualTo(1);
  }

  [Test]
  public async Task ExecuteAsync_ManualRenameRunsAsync() {
    const string oldClr = "Manual.Old.Name, TestApp";
    const string newClr = "Manual.New.Name, TestApp";

    await _seedEventStoreAsync(oldClr);

    var catalog = new FakeCatalog([]);
    var tool = new DapperEventTypeRenameTool(catalog, _connectionFactory!);

    tool.Rename(oldClr, newClr);
    await tool.ExecuteAsync();

    var rewritten = await _countAsync("wh_event_store", "event_type", newClr);
    await Assert.That(rewritten).IsEqualTo(1);
  }

  private async Task _seedRegistryAsync(string clrTypeName, string pinnedId) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      @"INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at)
        VALUES (@Clr, @PinnedId::uuid, 'event', NOW())",
      new { Clr = clrTypeName, PinnedId = pinnedId });
  }

  private async Task _seedEventStoreAsync(string clrTypeName) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      @"INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
        VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), @Clr, @Clr, '{}'::jsonb, '{}'::jsonb, 1, NOW())",
      new { Clr = clrTypeName });
  }

  private async Task _seedInboxAsync(string clrTypeName) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      @"INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, stream_id, partition_number, is_event, status, attempts)
        VALUES (gen_random_uuid(), 'test-handler', @Clr, '{}'::jsonb, '{}'::jsonb, gen_random_uuid(), 0, true, 1, 0)",
      new { Clr = clrTypeName });
  }

  private async Task _seedOutboxAsync(string clrTypeName) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      @"INSERT INTO wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, stream_id, partition_number, is_event)
        VALUES (gen_random_uuid(), 'test-destination', @Clr, @Clr, '{}'::jsonb, '{}'::jsonb, gen_random_uuid(), 0, true)",
      new { Clr = clrTypeName });
  }

  private async Task _seedMessageAssociationsAsync(string clrTypeName) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      @"INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name)
        VALUES (@Clr, 'event', 'test-target', 'test-service')",
      new { Clr = clrTypeName });
  }

  private async Task<int> _countAsync(string table, string column, string value) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    return await connection.ExecuteScalarAsync<int>(
      $"SELECT COUNT(*) FROM {table} WHERE {column} = @Value",
      new { Value = value });
  }

  private sealed class FakeCatalog(IReadOnlyList<MessageTypeCatalogEntry> entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private sealed record SampleType;
}
