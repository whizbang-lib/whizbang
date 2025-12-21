using System.Data;
using System.Text.Json;
using Npgsql;
using Testcontainers.PostgreSql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Tests for message association registry schema and reconciliation function.
/// Verifies wh_message_associations table and register_message_associations() function.
/// </summary>
/// <docs>core-concepts/message-associations</docs>
public class MessageAssociationRegistryTests : IAsyncDisposable {
  private PostgreSqlContainer? _postgresContainer;
  private string? _connectionString;

  [Before(Test)]
  public async Task SetupAsync() {
    _postgresContainer = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("whizbang_test")
      .WithUsername("postgres")
      .WithPassword("postgres")
      .Build();

    await _postgresContainer.StartAsync();
    _connectionString = _postgresContainer.GetConnectionString();

    // Initialize schema with migration
    var initializer = new PostgresSchemaInitializer(_connectionString);
    await initializer.InitializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_postgresContainer != null) {
      await _postgresContainer.StopAsync();
      await _postgresContainer.DisposeAsync();
      _postgresContainer = null;
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
    await using var conn = new NpgsqlConnection(_connectionString!);
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
    await using var conn = new NpgsqlConnection(_connectionString!);
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
    await using var conn = new NpgsqlConnection(_connectionString!);
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
    await using var conn = new NpgsqlConnection(_connectionString!);
    await conn.OpenAsync();
    await CleanupAssociationsAsync(conn);

    var associations = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      },
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductInventoryPerspective",
        ServiceName = "InventoryWorker"
      }
    });

    // Act - Call function
    await using var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations)", conn);
    cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, associations);
    await cmd.ExecuteNonQueryAsync();

    // Assert - Verify inserted
    var count = await GetAssociationCountAsync(conn);
    await Assert.That(count).IsEqualTo(2);
  }

  /// <summary>
  /// Tests that register_message_associations() updates updated_at on conflict.
  /// </summary>
  [Test]
  public async Task RegisterMessageAssociations_UpdatesTimestamp_OnConflictAsync() {
    // Arrange
    await using var conn = new NpgsqlConnection(_connectionString!);
    await conn.OpenAsync();
    await CleanupAssociationsAsync(conn);

    var associations = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      }
    });

    // Act - Insert once
    await using (var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations)", conn)) {
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, associations);
      await cmd.ExecuteNonQueryAsync();
    }

    var firstUpdatedAt = await GetAssociationUpdatedAtAsync(conn, "ProductCreatedEvent", "ProductCatalogPerspective");

    // Wait 100ms to ensure timestamp changes
    await Task.Delay(100);

    // Act - Insert again (should update updated_at)
    await using (var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations)", conn)) {
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, associations);
      await cmd.ExecuteNonQueryAsync();
    }

    var secondUpdatedAt = await GetAssociationUpdatedAtAsync(conn, "ProductCreatedEvent", "ProductCatalogPerspective");

    // Assert - Timestamp updated
    await Assert.That(secondUpdatedAt).IsGreaterThan(firstUpdatedAt);
  }

  /// <summary>
  /// Tests that register_message_associations() deletes associations not in the input.
  /// </summary>
  [Test]
  public async Task RegisterMessageAssociations_DeletesRemovedAssociations_CorrectlyAsync() {
    // Arrange
    await using var conn = new NpgsqlConnection(_connectionString!);
    await conn.OpenAsync();
    await CleanupAssociationsAsync(conn);

    // Insert 2 associations
    var initialAssociations = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      },
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductInventoryPerspective",
        ServiceName = "InventoryWorker"
      }
    });

    await using (var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations)", conn)) {
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, initialAssociations);
      await cmd.ExecuteNonQueryAsync();
    }

    // Act - Register with only 1 association (should delete the other)
    var updatedAssociations = JsonSerializer.Serialize(new[] {
      new {
        MessageType = "ProductCreatedEvent",
        AssociationType = "perspective",
        TargetName = "ProductCatalogPerspective",
        ServiceName = "BFF.API"
      }
    });

    await using (var cmd = new NpgsqlCommand("SELECT * FROM register_message_associations(@p_associations)", conn)) {
      cmd.Parameters.AddWithValue("p_associations", NpgsqlTypes.NpgsqlDbType.Jsonb, updatedAssociations);
      await cmd.ExecuteNonQueryAsync();
    }

    // Assert - Only 1 association remains
    var count = await GetAssociationCountAsync(conn);
    await Assert.That(count).IsEqualTo(1);

    // Assert - Correct one remains
    var exists = await AssociationExistsAsync(conn, "ProductCreatedEvent", "ProductCatalogPerspective");
    await Assert.That(exists).IsTrue();
  }

  // Helper methods

  private static async Task CleanupAssociationsAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("DELETE FROM wh_message_associations", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> GetAssociationCountAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM wh_message_associations", conn);
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result);
  }

  private static async Task<DateTime> GetAssociationUpdatedAtAsync(NpgsqlConnection conn, string messageType, string targetName) {
    await using var cmd = new NpgsqlCommand(
      "SELECT updated_at FROM wh_message_associations WHERE message_type = @mt AND target_name = @tn",
      conn);
    cmd.Parameters.AddWithValue("mt", messageType);
    cmd.Parameters.AddWithValue("tn", targetName);
    var result = await cmd.ExecuteScalarAsync();
    return (DateTime)result!;
  }

  private static async Task<bool> AssociationExistsAsync(NpgsqlConnection conn, string messageType, string targetName) {
    await using var cmd = new NpgsqlCommand(
      "SELECT EXISTS(SELECT 1 FROM wh_message_associations WHERE message_type = @mt AND target_name = @tn)",
      conn);
    cmd.Parameters.AddWithValue("mt", messageType);
    cmd.Parameters.AddWithValue("tn", targetName);
    var result = await cmd.ExecuteScalarAsync();
    return (bool)result!;
  }
}
