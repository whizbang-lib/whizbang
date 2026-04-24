using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Tests for message association registry schema and reconciliation function.
/// Verifies wh_message_associations table and register_message_associations() function.
/// Uses SharedPostgresContainer with per-test database isolation.
/// </summary>
/// <docs>core-concepts/message-associations</docs>
public class MessageAssociationRegistryTests : IAsyncDisposable {
  private string? _testDatabaseName;
  private string? _connectionString;

  [Before(Test)]
  public async Task SetupAsync() {
    // Initialize shared container (only starts once)
    await SharedPostgresContainer.InitializeAsync();

    // Create unique database for THIS test
    _testDatabaseName = $"test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    // Build connection string for the test database
    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName
    };
    _connectionString = builder.ConnectionString;

    // Initialize schema with migration
    var initializer = new PostgresSchemaInitializer(_connectionString);
    await initializer.InitializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    // Drop the test-specific database to clean up
    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();

        // Terminate connections to the test database
        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}'
          AND pid <> pg_backend_pid()");

        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch {
        // Ignore cleanup errors
      }

      _testDatabaseName = null;
      _connectionString = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// Verifies wh_message_associations table exists with correct schema.
  /// </summary>
  [Test]
  public async Task MessageAssociationsTable_Exists_WithCorrectSchemaAsync() {
    // Arrange
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();

    // Act - Query table schema
    await using var cmd = new NpgsqlCommand(@"
      SELECT column_name, data_type, is_nullable
      FROM information_schema.columns
      WHERE table_schema = 'public'
        AND table_name = 'wh_message_associations'
      ORDER BY ordinal_position",
      conn);

    var columns = new Dictionary<string, (string DataType, string IsNullable)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      columns[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
    }

    // Assert - Expected columns exist
    await Assert.That(columns).ContainsKey("id");
    await Assert.That(columns).ContainsKey("message_type");
    await Assert.That(columns).ContainsKey("association_type");
    await Assert.That(columns).ContainsKey("target_name");
    await Assert.That(columns).ContainsKey("service_name");
    await Assert.That(columns).ContainsKey("created_at");
    await Assert.That(columns).ContainsKey("updated_at");

    // Assert - Correct data types
    await Assert.That(columns["id"].DataType).IsEqualTo("uuid");
    await Assert.That(columns["message_type"].DataType).Contains("character varying");
    await Assert.That(columns["association_type"].DataType).Contains("character varying");
    await Assert.That(columns["target_name"].DataType).Contains("character varying");
    await Assert.That(columns["service_name"].DataType).Contains("character varying");
    await Assert.That(columns["created_at"].DataType).Contains("timestamp");
    await Assert.That(columns["updated_at"].DataType).Contains("timestamp");
  }

  /// <summary>
  /// Verifies unique constraint on (message_type, association_type, target_name, service_name).
  /// </summary>
  [Test]
  public async Task MessageAssociationsTable_HasUniqueConstraint_OnAssociationColumnsAsync() {
    // Arrange
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();

    // Act - Query constraints
    await using var cmd = new NpgsqlCommand(@"
      SELECT constraint_name, constraint_type
      FROM information_schema.table_constraints
      WHERE table_schema = 'public'
        AND table_name = 'wh_message_associations'
        AND constraint_type = 'UNIQUE'",
      conn);

    var constraints = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      constraints.Add(reader.GetString(0));
    }

    // Assert - Unique constraint exists
    await Assert.That(constraints.Count).IsGreaterThan(0);
  }

  /// <summary>
  /// Verifies register_message_associations() function exists and has correct signature.
  /// </summary>
  [Test]
  public async Task RegisterMessageAssociationsFunction_Exists_WithCorrectSignatureAsync() {
    // Arrange
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();

    // Act - Query function signature
    await using var cmd = new NpgsqlCommand(@"
      SELECT proname, pronargs, proargnames
      FROM pg_proc p
      JOIN pg_namespace n ON p.pronamespace = n.oid
      WHERE n.nspname = 'public'
        AND p.proname = 'register_message_associations'",
      conn);

    await using var reader = await cmd.ExecuteReaderAsync();
    var functionExists = await reader.ReadAsync();

    // Assert - Function exists
    await Assert.That(functionExists).IsTrue();
  }

  /// <summary>
  /// Tests that register_message_associations() inserts new associations.
  /// </summary>
  [Test]
  public async Task RegisterMessageAssociations_InsertsNewAssociations_SuccessfullyAsync() {
    // Arrange
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await _cleanupAssociationsAsync(conn);

    // Register each service's associations independently (orphan DELETE is scoped to p_service_name,
    // so cross-service registration must happen in separate calls — each service owns its own rows).
    var bffAssociations = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      }
    });
    var inventoryAssociations = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductInventoryPerspective",
        ServiceName = "InventoryWorker"
      }
    });

    // Act - Call function once per service
    await using (var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations, @p_service_name)", conn)) {
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, bffAssociations);
      cmd.Parameters.AddWithValue("p_service_name", "BFF.API");
      await cmd.ExecuteNonQueryAsync();
    }
    await using (var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations, @p_service_name)", conn)) {
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, inventoryAssociations);
      cmd.Parameters.AddWithValue("p_service_name", "InventoryWorker");
      await cmd.ExecuteNonQueryAsync();
    }

    // Assert - Verify both inserted
    var count = await _getAssociationCountAsync(conn);
    await Assert.That(count).IsEqualTo(2);
  }

  /// <summary>
  /// Tests that register_message_associations() updates updated_at on conflict.
  /// </summary>
  [Test]
  public async Task RegisterMessageAssociations_UpdatesTimestamp_OnConflictAsync() {
    // Arrange
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await _cleanupAssociationsAsync(conn);

    var associations = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      }
    });

    // Act - Insert once
    await using (var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations, @p_service_name)", conn)) {
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, associations);
      cmd.Parameters.AddWithValue("p_service_name", "BFF.API");
      await cmd.ExecuteNonQueryAsync();
    }

    var firstUpdatedAt = await _getAssociationUpdatedAtAsync(conn, "ProductCreatedEvent", "ProductCatalogPerspective");

    // Wait 100ms to ensure timestamp changes
    await Task.Delay(100);

    // Act - Insert again (should update updated_at)
    await using (var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations, @p_service_name)", conn)) {
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, associations);
      cmd.Parameters.AddWithValue("p_service_name", "BFF.API");
      await cmd.ExecuteNonQueryAsync();
    }

    var secondUpdatedAt = await _getAssociationUpdatedAtAsync(conn, "ProductCreatedEvent", "ProductCatalogPerspective");

    // Assert - Timestamp updated
    await Assert.That(secondUpdatedAt).IsGreaterThan(firstUpdatedAt);
  }

  /// <summary>
  /// Tests that register_message_associations() deletes associations not in the input, scoped
  /// to the calling p_service_name. Rows owned by other services must NOT be touched (that
  /// scoping is the whole point of p_service_name — see migration 008).
  /// </summary>
  [Test]
  public async Task RegisterMessageAssociations_DeletesRemovedAssociations_CorrectlyAsync() {
    // Arrange
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await _cleanupAssociationsAsync(conn);

    // Seed: BFF.API owns two associations; InventoryWorker owns one.
    // A subsequent BFF.API re-registration with one of its two must delete only BFF's orphan,
    // not InventoryWorker's row — that's the defense-in-depth behavior the fix introduces.
    var initialBffAssociations = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      },
      new {
        MessageType = "ProductUpdatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      }
    });
    var initialInventoryAssociations = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductInventoryPerspective",
        ServiceName = "InventoryWorker"
      }
    });

    await using (var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations, @p_service_name)", conn)) {
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, initialBffAssociations);
      cmd.Parameters.AddWithValue("p_service_name", "BFF.API");
      await cmd.ExecuteNonQueryAsync();
    }
    await using (var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations, @p_service_name)", conn)) {
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, initialInventoryAssociations);
      cmd.Parameters.AddWithValue("p_service_name", "InventoryWorker");
      await cmd.ExecuteNonQueryAsync();
    }

    // Act - BFF.API re-registers with only ProductCreatedEvent; ProductUpdatedEvent should be
    // deleted as an orphan within BFF.API's scope. InventoryWorker's row must stay put.
    var updatedBffAssociations = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      }
    });

    await using (var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations, @p_service_name)", conn)) {
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, updatedBffAssociations);
      cmd.Parameters.AddWithValue("p_service_name", "BFF.API");
      await cmd.ExecuteNonQueryAsync();
    }

    // Assert - BFF orphan deleted, BFF remaining row kept, InventoryWorker row untouched.
    var count = await _getAssociationCountAsync(conn);
    await Assert.That(count).IsEqualTo(2);
    await Assert.That(await _associationExistsAsync(conn, "ProductCreatedEvent", "ProductCatalogPerspective")).IsTrue();
    await Assert.That(await _associationExistsAsync(conn, "ProductCreatedEvent", "ProductInventoryPerspective")).IsTrue();
    await Assert.That(await _associationExistsAsync(conn, "ProductUpdatedEvent", "ProductCatalogPerspective")).IsFalse();
  }

  /// <summary>
  /// Tests that duplicate entries in the JSON input cause a PostgreSQL error.
  /// This documents why the generator must deduplicate perspective associations.
  /// The error "ON CONFLICT DO UPDATE command cannot affect row a second time" occurs
  /// when the same key appears multiple times in a single INSERT statement.
  /// </summary>
  [Test]
  public async Task RegisterMessageAssociations_DuplicateEntriesInJson_ThrowsPostgresExceptionAsync() {
    // Arrange
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await _cleanupAssociationsAsync(conn);

    // JSON with duplicate entries (same message_type, association_type, target_name, service_name)
    var associationsWithDuplicates = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      },
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      } // Duplicate!
    });

    // Act & Assert - Should throw PostgresException with SQLSTATE 21000
    await using var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations, @p_service_name)", conn);
    cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, associationsWithDuplicates);
    cmd.Parameters.AddWithValue("p_service_name", "BFF.API");

    var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(
      async () => await cmd.ExecuteNonQueryAsync());

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.SqlState).IsEqualTo("21000"); // cardinality_violation
    await Assert.That(exception.Message).Contains("ON CONFLICT DO UPDATE command cannot affect row a second time");
  }

  /// <summary>
  /// Tests that registering associations is idempotent - calling it multiple times
  /// with the same data succeeds without errors.
  /// </summary>
  [Test]
  public async Task RegisterMessageAssociations_CalledTwice_IsIdempotentAsync() {
    // Arrange
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await _cleanupAssociationsAsync(conn);

    var associations = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      },
      new {
        MessageType = "ProductUpdatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      }
    });

    // Act - Call twice
    for (int i = 0; i < 2; i++) {
      await using var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations, @p_service_name)", conn);
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, associations);
      cmd.Parameters.AddWithValue("p_service_name", "BFF.API");
      await cmd.ExecuteNonQueryAsync();
    }

    // Assert - Should have exactly 2 associations (no duplicates from multiple calls)
    var count = await _getAssociationCountAsync(conn);
    await Assert.That(count).IsEqualTo(2);
  }

  // Helper methods

  private static async Task _cleanupAssociationsAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("DELETE FROM wh_message_associations", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> _getAssociationCountAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM wh_message_associations", conn);
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result, CultureInfo.InvariantCulture);
  }

  private static async Task<DateTime> _getAssociationUpdatedAtAsync(NpgsqlConnection conn, string messageType, string targetName) {
    await using var cmd = new NpgsqlCommand(
      "SELECT updated_at FROM wh_message_associations WHERE message_type = @mt AND target_name = @tn",
      conn);
    cmd.Parameters.AddWithValue("mt", messageType);
    cmd.Parameters.AddWithValue("tn", targetName);
    var result = await cmd.ExecuteScalarAsync();
    return (DateTime)result!;
  }

  private static async Task<bool> _associationExistsAsync(NpgsqlConnection conn, string messageType, string targetName) {
    await using var cmd = new NpgsqlCommand(
      "SELECT EXISTS(SELECT 1 FROM wh_message_associations WHERE message_type = @mt AND target_name = @tn)",
      conn);
    cmd.Parameters.AddWithValue("mt", messageType);
    cmd.Parameters.AddWithValue("tn", targetName);
    var result = await cmd.ExecuteScalarAsync();
    return (bool)result!;
  }
}
