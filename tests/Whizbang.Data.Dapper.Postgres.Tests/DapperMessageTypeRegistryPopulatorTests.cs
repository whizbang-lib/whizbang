using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Integration tests for DapperMessageTypeRegistryPopulator.
/// Verifies upsert semantics against wh_message_type_registry — pinned types keyed by
/// pinned_id, unpinned types keyed by clr_type_name, and drift detection logs but does
/// not overwrite.
/// </summary>
[Category("Integration")]
public class DapperMessageTypeRegistryPopulatorTests : IAsyncDisposable {
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
  public async Task PopulateAsync_InsertsPinnedTypesAsync() {
    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.PinnedEvent, Sample", "event", "11111111-1111-1111-1111-111111111111"),
      new MessageTypeCatalogEntry(typeof(SamplePinnedCmd), "Sample.PinnedCommand, Sample", "command", "22222222-2222-2222-2222-222222222222"),
    ]);

    var populator = new DapperMessageTypeRegistryPopulator(catalog, _connectionFactory!);
    await populator.PopulateAsync();

    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(2);
    await Assert.That(rows.Any(r => r.PinnedId == new Guid("11111111-1111-1111-1111-111111111111") && r.ClrTypeName == "Sample.PinnedEvent, Sample" && r.Kind == "event")).IsTrue();
    await Assert.That(rows.Any(r => r.PinnedId == new Guid("22222222-2222-2222-2222-222222222222") && r.ClrTypeName == "Sample.PinnedCommand, Sample" && r.Kind == "command")).IsTrue();
  }

  [Test]
  public async Task PopulateAsync_InsertsUnpinnedTypesWithNullPinnedIdAsync() {
    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.UnpinnedEvent, Sample", "event", null),
    ]);

    var populator = new DapperMessageTypeRegistryPopulator(catalog, _connectionFactory!);
    await populator.PopulateAsync();

    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].PinnedId).IsNull();
    await Assert.That(rows[0].ClrTypeName).IsEqualTo("Sample.UnpinnedEvent, Sample");
    await Assert.That(rows[0].Kind).IsEqualTo("event");
  }

  [Test]
  public async Task PopulateAsync_IsIdempotent_UpdatesTimestampOnReRunAsync() {
    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.PinnedEvent, Sample", "event", "11111111-1111-1111-1111-111111111111"),
    ]);

    var populator = new DapperMessageTypeRegistryPopulator(catalog, _connectionFactory!);
    await populator.PopulateAsync();
    var firstRow = (await _queryRegistryAsync()).Single();

    await Task.Delay(50);
    await populator.PopulateAsync();
    var secondRow = (await _queryRegistryAsync()).Single();

    await Assert.That(secondRow.TypeId).IsEqualTo(firstRow.TypeId);
    await Assert.That(secondRow.UpdatedAt >= firstRow.UpdatedAt).IsTrue();
  }

  [Test]
  public async Task PopulateAsync_PinnedRenameDetected_DoesNotOverwriteAsync() {
    // Initial run: pinned type with CLR name "Old.Namespace.FooEvent"
    var catalog1 = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Old.Namespace.FooEvent, Sample", "event", "11111111-1111-1111-1111-111111111111"),
    ]);
    await new DapperMessageTypeRegistryPopulator(catalog1, _connectionFactory!).PopulateAsync();

    // Subsequent run: same pinned_id, different CLR name (namespace was renamed)
    var catalog2 = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "New.Namespace.FooEvent, Sample", "event", "11111111-1111-1111-1111-111111111111"),
    ]);
    await new DapperMessageTypeRegistryPopulator(catalog2, _connectionFactory!).PopulateAsync();

    // Registry row remains pointing at the old CLR name — drift is reported, not reconciled.
    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].ClrTypeName).IsEqualTo("Old.Namespace.FooEvent, Sample");
    await Assert.That(rows[0].PinnedId).IsEqualTo(new Guid("11111111-1111-1111-1111-111111111111"));
  }

  [Test]
  public async Task PopulateAsync_PinnedDottedEncoding_NormalizedToPlusAsync() {
    const string id = "11111111-1111-1111-1111-111111111111";
    // Legacy state: stored '.'-nested (the old MessageTypeCatalogGenerator wrote the C# display form).
    await new DapperMessageTypeRegistryPopulator(new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Acme.Domain.OrderContracts.Projection", "perspective", id),
    ]), _connectionFactory!).PopulateAsync();

    // Current catalog reports the CLR '+'-nested form for the SAME pinned type.
    await new DapperMessageTypeRegistryPopulator(new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Acme.Domain.OrderContracts+Projection", "perspective", id),
    ]), _connectionFactory!).PopulateAsync();

    // Reconciled in place ('.'-encoding -> '+'), NOT flagged as drift. This is what closes the last gap:
    // perspective types with no message association (no DB '+' oracle) are still normalized via the catalog.
    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].ClrTypeName).IsEqualTo("Acme.Domain.OrderContracts+Projection");
    await Assert.That(rows[0].PinnedId).IsEqualTo(new Guid(id));
  }

  [Test]
  public async Task PopulateAsync_DottedEncoding_WithPreexistingPlusRow_DedupsWithoutCollisionAsync() {
    const string dottedId = "11111111-1111-1111-1111-111111111111";
    // A stale '.'-row (this pinned id) AND the canonical '+'-row (a different identity) already coexist —
    // the exact production shape. A naive rename '.'->'+' would violate the clr_type_name primary key.
    await using (var conn = new NpgsqlConnection(_connectionString)) {
      await conn.OpenAsync();
      await conn.ExecuteAsync(
        @"INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at) VALUES
          ('Acme.Domain.OrderContracts.Projection', @Dotted::uuid, 'perspective', NOW()),
          ('Acme.Domain.OrderContracts+Projection', gen_random_uuid(), 'perspective', NOW())",
        new { Dotted = dottedId });
    }

    await new DapperMessageTypeRegistryPopulator(new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Acme.Domain.OrderContracts+Projection", "perspective", dottedId),
    ]), _connectionFactory!).PopulateAsync();

    // The stale '+' duplicate is dropped and the pinned row is normalized — exactly one '+' row remains.
    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].ClrTypeName).IsEqualTo("Acme.Domain.OrderContracts+Projection");
    await Assert.That(rows[0].PinnedId).IsEqualTo(new Guid(dottedId));
  }

  [Test]
  public async Task PopulateAsync_MixedPinnedAndUnpinned_BothInsertedAsync() {
    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.Pinned, Sample", "event", "11111111-1111-1111-1111-111111111111"),
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.Unpinned, Sample", "event", null),
      new MessageTypeCatalogEntry(typeof(SamplePinnedCmd), "Sample.Perspective, Sample", "perspective", null),
    ]);

    var populator = new DapperMessageTypeRegistryPopulator(catalog, _connectionFactory!);
    await populator.PopulateAsync();

    var rows = await _queryRegistryAsync();
    await Assert.That(rows).Count().IsEqualTo(3);
    await Assert.That(rows.Count(r => r.PinnedId is null)).IsEqualTo(2);
    await Assert.That(rows.Count(r => r.PinnedId is not null)).IsEqualTo(1);
  }

  [Test]
  public async Task PopulateAsync_SummaryLog_IncludesPinnedAndUnpinnedCountsAsync() {
    var catalog = new FakeCatalog([
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.Pinned1, Sample", "event", "11111111-1111-1111-1111-111111111111"),
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.Pinned2, Sample", "command", "22222222-2222-2222-2222-222222222222"),
      new MessageTypeCatalogEntry(typeof(SamplePinned), "Sample.Unpinned1, Sample", "event", null),
    ]);
    var logger = new CapturingLogger<DapperMessageTypeRegistryPopulator>();

    var populator = new DapperMessageTypeRegistryPopulator(catalog, _connectionFactory!, logger);
    await populator.PopulateAsync();

    var summary = logger.Messages.FirstOrDefault(m => m.Contains("Message type registry populated", StringComparison.Ordinal));
    await Assert.That(summary).IsNotNull();
    await Assert.That(summary!).Contains("3 entries");
    await Assert.That(summary!).Contains("2 pinned");
    await Assert.That(summary!).Contains("1 unpinned");
  }

  [Test]
  public async Task PopulateAsync_EmptyCatalog_LeavesRegistryUntouchedAsync() {
    var populator = new DapperMessageTypeRegistryPopulator(new FakeCatalog([]), _connectionFactory!);
    await populator.PopulateAsync();

    var rows = await _queryRegistryAsync();
    await Assert.That(rows).IsEmpty();
  }

  private async Task<List<RegistryRow>> _queryRegistryAsync() {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    var rows = await connection.QueryAsync<RegistryRow>(
      "SELECT type_id AS TypeId, clr_type_name AS ClrTypeName, pinned_id AS PinnedId, kind AS Kind, updated_at AS UpdatedAt FROM wh_message_type_registry ORDER BY clr_type_name");
    return [.. rows];
  }

  private sealed record RegistryRow(Guid TypeId, string ClrTypeName, Guid? PinnedId, string Kind, DateTimeOffset UpdatedAt);

  private sealed class FakeCatalog(IReadOnlyList<MessageTypeCatalogEntry> entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private sealed class CapturingLogger<T> : ILogger<T> {
    public List<string> Messages { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Messages.Add(formatter(state, exception));
    }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  private sealed record SamplePinned;
  private sealed record SamplePinnedCmd;
}
