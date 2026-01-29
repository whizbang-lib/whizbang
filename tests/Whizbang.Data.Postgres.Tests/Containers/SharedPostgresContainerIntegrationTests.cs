using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Data.Postgres.Tests.Containers;

/// <summary>
/// Integration tests for <see cref="SharedPostgresContainer"/>.
/// Tests container initialization, connection management, and per-test database isolation.
/// </summary>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class SharedPostgresContainerIntegrationTests {
  [Before(Test)]
  public async Task SetupAsync() {
    // Ensure container is initialized before each test
    await SharedPostgresContainer.InitializeAsync();
  }

  [Test]
  [Timeout(60000)]
  public async Task InitializeAsync_StartsContainer_IsInitializedReturnsTrueAsync(CancellationToken cancellationToken) {
    // Act
    await SharedPostgresContainer.InitializeAsync(cancellationToken);

    // Assert
    await Assert.That(SharedPostgresContainer.IsInitialized).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task ConnectionString_AfterInitialize_IsValidPostgresConnectionStringAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedPostgresContainer.InitializeAsync(cancellationToken);

    // Act
    var connectionString = SharedPostgresContainer.ConnectionString;

    // Assert
    await Assert.That(connectionString).IsNotNull();
    await Assert.That(connectionString).Contains("Host=localhost");
    await Assert.That(connectionString).Contains("Database=whizbang_test");
    await Assert.That(connectionString).Contains("Username=whizbang_user");
  }

  [Test]
  [Timeout(60000)]
  public async Task ConnectionString_CanBeUsedToConnectAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var connectionString = SharedPostgresContainer.ConnectionString;

    // Act
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    // Assert
    await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Open);
  }

  [Test]
  [Timeout(60000)]
  public async Task InitializeAsync_CalledMultipleTimes_ReusesContainerAsync(CancellationToken cancellationToken) {
    // Act - Call initialize multiple times
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var firstConnectionString = SharedPostgresContainer.ConnectionString;

    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var secondConnectionString = SharedPostgresContainer.ConnectionString;

    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var thirdConnectionString = SharedPostgresContainer.ConnectionString;

    // Assert - All connection strings should be the same (same container)
    await Assert.That(secondConnectionString).IsEqualTo(firstConnectionString);
    await Assert.That(thirdConnectionString).IsEqualTo(firstConnectionString);
  }

  [Test]
  [Timeout(60000)]
  public async Task GetPerTestDatabaseConnectionString_ReturnsUniqueDatabaseAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedPostgresContainer.InitializeAsync(cancellationToken);

    // Act
    var connString1 = SharedPostgresContainer.GetPerTestDatabaseConnectionString();
    var connString2 = SharedPostgresContainer.GetPerTestDatabaseConnectionString();
    var connString3 = SharedPostgresContainer.GetPerTestDatabaseConnectionString();

    // Assert - Each should have unique database name
    await Assert.That(connString1).IsNotEqualTo(connString2);
    await Assert.That(connString2).IsNotEqualTo(connString3);
    await Assert.That(connString1).IsNotEqualTo(connString3);

    // All should contain test_ prefix
    await Assert.That(connString1).Contains("Database=test_");
    await Assert.That(connString2).Contains("Database=test_");
    await Assert.That(connString3).Contains("Database=test_");
  }

  [Test]
  [Timeout(60000)]
  public async Task GetPerTestDatabaseConnectionString_HasSmallPoolSizeAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedPostgresContainer.InitializeAsync(cancellationToken);

    // Act
    var connString = SharedPostgresContainer.GetPerTestDatabaseConnectionString();

    // Assert - Should have small pool sizes
    await Assert.That(connString).Contains("Minimum Pool Size=0");
    await Assert.That(connString).Contains("Maximum Pool Size=2");
  }

  [Test]
  [Timeout(60000)]
  public async Task InitializeAsync_WithCancellation_RespectsTokenAsync(CancellationToken cancellationToken) {
    // Act - Initialize (should succeed if already initialized, or complete quickly)
    await SharedPostgresContainer.InitializeAsync(cancellationToken);

    // Assert - Should be initialized
    await Assert.That(SharedPostgresContainer.IsInitialized).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task DisposeAsync_ResetsState_AllowsReinitializationAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    _ = SharedPostgresContainer.ConnectionString; // Access to verify it's available

    // Act
    await SharedPostgresContainer.DisposeAsync();

    // Assert - State should be reset
    await Assert.That(SharedPostgresContainer.IsInitialized).IsFalse();

    // Reinitialize
    await SharedPostgresContainer.InitializeAsync(cancellationToken);

    // Should be initialized again
    await Assert.That(SharedPostgresContainer.IsInitialized).IsTrue();
    await Assert.That(SharedPostgresContainer.ConnectionString).IsNotNull();
  }

  [Test]
  [Timeout(60000)]
  public async Task InitializeAsync_ConcurrentCalls_OnlyInitializesOnceAsync(CancellationToken cancellationToken) {
    // Arrange - Reset state first
    await SharedPostgresContainer.DisposeAsync();

    // Act - Call initialize concurrently
    var tasks = new List<Task>();
    for (var i = 0; i < 5; i++) {
      tasks.Add(SharedPostgresContainer.InitializeAsync(cancellationToken));
    }

    await Task.WhenAll(tasks);

    // Assert - Should be initialized and have valid connection string
    await Assert.That(SharedPostgresContainer.IsInitialized).IsTrue();
    await Assert.That(SharedPostgresContainer.ConnectionString).IsNotNull();
  }

  [Test]
  [Timeout(60000)]
  public async Task GetPerTestDatabaseConnectionString_BeforeInitialize_ThrowsAsync(CancellationToken cancellationToken) {
    // Arrange - Dispose to reset state
    await SharedPostgresContainer.DisposeAsync();

    // Act & Assert
    await Assert.That(() => SharedPostgresContainer.GetPerTestDatabaseConnectionString())
        .Throws<InvalidOperationException>();

    // Reinitialize for other tests
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
  }

  [Test]
  [Timeout(60000)]
  public async Task ConnectionString_BeforeInitialize_ThrowsAsync(CancellationToken cancellationToken) {
    // Arrange - Dispose to reset state
    await SharedPostgresContainer.DisposeAsync();

    // Act & Assert
    await Assert.That(() => SharedPostgresContainer.ConnectionString)
        .Throws<InvalidOperationException>();

    // Reinitialize for other tests
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
  }

  [Test]
  [Timeout(60000)]
  public async Task InitializeAsync_AfterDisposeAndReconnect_WorksCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    await SharedPostgresContainer.DisposeAsync();

    // Act - Reinitialize
    await SharedPostgresContainer.InitializeAsync(cancellationToken);

    // Assert - Can connect
    await using var connection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Open);
  }

  [Test]
  [Timeout(60000)]
  public async Task DatabaseConnection_CanExecuteSimpleQueryAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var connectionString = SharedPostgresContainer.ConnectionString;

    // Act
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    await using var cmd = new NpgsqlCommand("SELECT 1 + 1", connection);
    var result = await cmd.ExecuteScalarAsync(cancellationToken);

    // Assert
    await Assert.That(result).IsEqualTo(2);
  }

  [Test]
  [Timeout(60000)]
  public async Task PerTestDatabase_CanCreateAndQueryTablesAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var perTestConnString = SharedPostgresContainer.GetPerTestDatabaseConnectionString();

    // Need to create the database first since it doesn't exist
    var builder = new NpgsqlConnectionStringBuilder(perTestConnString);
    var dbName = builder.Database;
    builder.Database = "whizbang_test"; // Connect to main database to create test db

    await using var adminConn = new NpgsqlConnection(builder.ConnectionString);
    await adminConn.OpenAsync(cancellationToken);
    await using (var createCmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", adminConn)) {
      await createCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // Act - Connect to the new database and create a table
    await using var connection = new NpgsqlConnection(perTestConnString);
    await connection.OpenAsync(cancellationToken);

    await using (var createTableCmd = new NpgsqlCommand(
        "CREATE TABLE test_table (id SERIAL PRIMARY KEY, name TEXT)", connection)) {
      await createTableCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    await using (var insertCmd = new NpgsqlCommand(
        "INSERT INTO test_table (name) VALUES ('test')", connection)) {
      await insertCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    await using var selectCmd = new NpgsqlCommand("SELECT name FROM test_table", connection);
    var result = await selectCmd.ExecuteScalarAsync(cancellationToken);

    // Assert
    await Assert.That(result).IsEqualTo("test");

    // Cleanup - drop the test database
    await connection.CloseAsync();
    NpgsqlConnection.ClearAllPools();
    await using var dropCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\"", adminConn);
    await dropCmd.ExecuteNonQueryAsync(cancellationToken);
  }
}
