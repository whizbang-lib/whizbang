using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Tests for PostgresDatabaseReadinessCheck - database connectivity and schema readiness verification.
/// Follows TDD RED-GREEN-REFACTOR pattern.
/// </summary>
public class PostgresDatabaseReadinessCheckTests : PostgresTestBase {
  [Test]
  public async Task IsReadyAsync_WithRunningDatabaseAndSchema_ReturnsTrueAsync() {
    // Arrange - PostgresTestBase sets up container with schema
    var readinessCheck = new PostgresDatabaseReadinessCheck(
      ConnectionString,
      NullLogger<PostgresDatabaseReadinessCheck>.Instance
    );

    // Act
    var isReady = await readinessCheck.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsTrue()
      .Because("Database is running and schema is initialized");
  }

  [Test]
  public async Task IsReadyAsync_WithInvalid_connectionString_ReturnsFalseAsync() {
    // Arrange
    const string invalid_connectionString = "Host=localhost;Port=9999;Database=nonexistent;Username=invalid;Password=invalid;Timeout=1;";
    var readinessCheck = new PostgresDatabaseReadinessCheck(
      invalid_connectionString,
      NullLogger<PostgresDatabaseReadinessCheck>.Instance
    );

    // Act
    var isReady = await readinessCheck.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsFalse()
      .Because("Database connection should fail with invalid connection string");
  }

  [Test]
  public async Task IsReadyAsync_WithMissingTables_ReturnsFalseAsync() {
    // Arrange - Create a fresh database without Whizbang schema
    await using var testContainer = new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:17-alpine")
      .WithDatabase("empty_test")
      .WithUsername("postgres")
      .WithPassword("postgres")
      .Build();

    await testContainer.StartAsync();

    try {
      var connectionString = testContainer.GetConnectionString();
      var readinessCheck = new PostgresDatabaseReadinessCheck(
        connectionString,
        NullLogger<PostgresDatabaseReadinessCheck>.Instance
      );

      // Act
      var isReady = await readinessCheck.IsReadyAsync();

      // Assert
      await Assert.That(isReady).IsFalse()
        .Because("Required Whizbang tables (wh_inbox, wh_outbox, wh_event_store) do not exist");
    } finally {
      await testContainer.StopAsync();
    }
  }

  [Test]
  public async Task IsReadyAsync_MultipleCalls_ReturnsConsistentResultAsync() {
    // Arrange
    var readinessCheck = new PostgresDatabaseReadinessCheck(
      ConnectionString,
      NullLogger<PostgresDatabaseReadinessCheck>.Instance
    );

    // Act - Call multiple times
    var result1 = await readinessCheck.IsReadyAsync();
    var result2 = await readinessCheck.IsReadyAsync();
    var result3 = await readinessCheck.IsReadyAsync();

    // Assert
    await Assert.That(result1).IsTrue()
      .Because("First call should succeed when database is ready");
    await Assert.That(result2).IsTrue()
      .Because("Second call should succeed when database is ready");
    await Assert.That(result3).IsTrue()
      .Because("Third call should succeed when database is ready");
  }

  [Test]
  public async Task IsReadyAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var readinessCheck = new PostgresDatabaseReadinessCheck(
      ConnectionString,
      NullLogger<PostgresDatabaseReadinessCheck>.Instance
    );
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => await readinessCheck.IsReadyAsync(cts.Token))
      .ThrowsExactly<OperationCanceledException>()
      .Because("Cancelled operations should throw OperationCanceledException");
  }

  [Test]
  public async Task IsReadyAsync_ChecksAllRequiredTables_VerifiesInboxOutboxEventStoreAsync() {
    // Arrange
    var readinessCheck = new PostgresDatabaseReadinessCheck(
      ConnectionString,
      NullLogger<PostgresDatabaseReadinessCheck>.Instance
    );

    // Act
    var isReady = await readinessCheck.IsReadyAsync();

    // Assert - Verify the three core tables exist
    await Assert.That(isReady).IsTrue()
      .Because("All three required tables (wh_inbox, wh_outbox, wh_event_store) should exist");

    // Verify tables exist directly in database
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    const string tableCountSql = @"
      SELECT COUNT(*)
      FROM information_schema.tables
      WHERE table_schema = 'public'
        AND table_name IN ('wh_inbox', 'wh_outbox', 'wh_event_store')";

    var tableCount = await connection.QuerySingleAsync<int>(tableCountSql);

    await Assert.That(tableCount).IsEqualTo(3)
      .Because("wh_inbox, wh_outbox, and wh_event_store tables should all exist");
  }

  [Test]
  public async Task IsReadyAsync_WithMissingFunctions_ReturnsFalseAsync() {
    // Arrange - Create a fresh database with tables but WITHOUT the process_work_batch function
    await using var testContainer = new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:17-alpine")
      .WithDatabase("tables_only_test")
      .WithUsername("postgres")
      .WithPassword("postgres")
      .Build();

    await testContainer.StartAsync();

    try {
      var connectionString = testContainer.GetConnectionString();

      // Create only the required tables (without the task schema and functions)
      await using var setupConnection = new Npgsql.NpgsqlConnection(connectionString);
      await setupConnection.OpenAsync();

      const string createTablesSql = @"
        CREATE TABLE wh_inbox (id SERIAL PRIMARY KEY);
        CREATE TABLE wh_outbox (id SERIAL PRIMARY KEY);
        CREATE TABLE wh_event_store (id SERIAL PRIMARY KEY);";

      await setupConnection.ExecuteAsync(createTablesSql);

      var readinessCheck = new PostgresDatabaseReadinessCheck(
        connectionString,
        NullLogger<PostgresDatabaseReadinessCheck>.Instance
      );

      // Act
      var isReady = await readinessCheck.IsReadyAsync();

      // Assert
      await Assert.That(isReady).IsFalse()
        .Because("Required function 'task.process_work_batch' does not exist - workers would fail");
    } finally {
      await testContainer.StopAsync();
    }
  }

  [Test]
  public async Task IsReadyAsync_WithAllRequiredFunctions_ReturnsTrueAsync() {
    // Arrange - Use the test database which has all functions
    var readinessCheck = new PostgresDatabaseReadinessCheck(
      ConnectionString,
      NullLogger<PostgresDatabaseReadinessCheck>.Instance
    );

    // Verify the process_work_batch function exists in test database (in public schema)
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    const string functionCountSql = @"
      SELECT COUNT(*)
      FROM information_schema.routines
      WHERE routine_schema = 'public'
        AND routine_name = 'process_work_batch'
        AND routine_type = 'FUNCTION'";

    var functionCount = await connection.QuerySingleAsync<int>(functionCountSql);
    await Assert.That(functionCount).IsEqualTo(1)
      .Because("Test database should have public.process_work_batch function");

    // Act
    var isReady = await readinessCheck.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsTrue()
      .Because("All required tables AND functions exist");
  }

  // ============================================================================
  // NpgsqlDataSource Constructor Tests (Fix for password stripping bug)
  // ============================================================================

  /// <summary>
  /// THE BUG FIX TEST: Verifies that using NpgsqlDataSource retains credentials
  /// even though dataSource.ConnectionString strips the password for security.
  /// This is the core test for the password stripping bug fix.
  /// </summary>
  [Test]
  public async Task IsReadyAsync_WithDataSourceFromPasswordProtectedConnection_AuthenticatesSuccessfullyAsync() {
    // Arrange - Build NpgsqlDataSource from full connection string (with password)
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionString);
    await using var dataSource = dataSourceBuilder.Build();

    // Verify Npgsql's security behavior: ConnectionString property strips password
    // This is the root cause of the bug - extracting ConnectionString loses credentials
    await Assert.That(dataSource.ConnectionString).DoesNotContain("Password=")
      .Because("Npgsql strips password from ConnectionString property for security");

    // Act - Create readiness check using DataSource (not connection string)
    var readinessCheck = new PostgresDatabaseReadinessCheck(
      dataSource,
      NullLogger<PostgresDatabaseReadinessCheck>.Instance
    );
    var isReady = await readinessCheck.IsReadyAsync();

    // Assert - Should authenticate successfully using DataSource.CreateConnection()
    await Assert.That(isReady).IsTrue()
      .Because("DataSource.CreateConnection() retains credentials internally, unlike ConnectionString property");
  }

  /// <summary>
  /// Tests that the NpgsqlDataSource constructor properly validates null input.
  /// </summary>
  [Test]
  public async Task Constructor_WithNullDataSource_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new PostgresDatabaseReadinessCheck(
      (NpgsqlDataSource)null!,
      NullLogger<PostgresDatabaseReadinessCheck>.Instance
    )).ThrowsExactly<ArgumentNullException>()
      .Because("Null data source should be rejected");
  }

  /// <summary>
  /// Tests backward compatibility: connection string constructor still works
  /// for the Dapper path which receives the full connection string.
  /// </summary>
  [Test]
  public async Task IsReadyAsync_WithConnectionStringConstructor_MaintainsBackwardCompatibilityAsync() {
    // Arrange - Use connection string constructor (existing Dapper path)
    var readinessCheck = new PostgresDatabaseReadinessCheck(
      ConnectionString,
      NullLogger<PostgresDatabaseReadinessCheck>.Instance
    );

    // Act
    var isReady = await readinessCheck.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsTrue()
      .Because("Connection string constructor must continue working for Dapper path");
  }
}
