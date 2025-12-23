using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Postgres.Tests;

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
    var invalid_connectionString = "Host=localhost;Port=9999;Database=nonexistent;Username=invalid;Password=invalid;Timeout=1;";
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
    await using var testContainer = new Testcontainers.PostgreSql.PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
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
    var tableCountSql = @"
      SELECT COUNT(*)
      FROM information_schema.tables
      WHERE table_schema = 'public'
        AND table_name IN ('wh_inbox', 'wh_outbox', 'wh_event_store')";

    var tableCount = await connection.QuerySingleAsync<int>(tableCountSql);

    await Assert.That(tableCount).IsEqualTo(3)
      .Because("wh_inbox, wh_outbox, and wh_event_store tables should all exist");
  }
}
