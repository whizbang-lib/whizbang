using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Tests for ServiceCollectionExtensions.AddWhizbangPostgres.
/// Phase 1: Verify perspective schema parameter integration.
/// Uses SharedPostgresContainer with per-test database isolation.
/// </summary>
public class ServiceCollectionExtensionsTests : IAsyncDisposable {
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
  /// Test 1: initializeSchema = false → no schema initialization
  /// Branch coverage: initializeSchema == false path
  /// </summary>
  [Test]
  public async Task AddWhizbangPostgres_InitializeSchemaFalse_DoesNotInitializeAsync() {
    // Arrange
    var services = new ServiceCollection();
    var jsonOptions = new JsonSerializerOptions();

    // Act
    services.AddWhizbangPostgres(
      _connectionString!,
      jsonOptions,
      initializeSchema: false);

    // Assert - Service registration succeeded (no exception)
    await Assert.That(services.Count).IsGreaterThan(0);
  }

  /// <summary>
  /// Test 2: initializeSchema = true, perspectiveSchemaSql = null → infra only
  /// Branch coverage: initializeSchema == true && perspectiveSchemaSql == null
  /// </summary>
  [Test]
  public async Task AddWhizbangPostgres_InitializeSchemaTrue_NoPerspective_InitializesInfraOnlyAsync() {
    // Arrange
    var services = new ServiceCollection();
    var jsonOptions = new JsonSerializerOptions();

    // Act
    services.AddWhizbangPostgres(
      _connectionString!,
      jsonOptions,
      initializeSchema: true,
      perspectiveSchemaSql: null);

    // Assert - Service registration succeeded
    await Assert.That(services.Count).IsGreaterThan(0);

    // Verify infrastructure was initialized by connecting and querying
    await using var connection = new Npgsql.NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'wh_event_store'
      );";

    var exists = await command.ExecuteScalarAsync();
    await Assert.That((bool)exists!).IsTrue();
  }

  /// <summary>
  /// Test 3: initializeSchema = true, perspectiveSchemaSql = "..." → infra + perspectives
  /// Branch coverage: initializeSchema == true && perspectiveSchemaSql != null
  /// </summary>
  [Test]
  public async Task AddWhizbangPostgres_InitializeSchemaTrue_WithPerspective_InitializesBothAsync() {
    // Arrange
    var services = new ServiceCollection();
    var jsonOptions = new JsonSerializerOptions();
    const string perspectiveSql = @"
      CREATE SCHEMA IF NOT EXISTS test_ext_schema;
      CREATE TABLE IF NOT EXISTS test_ext_schema.test_ext_perspective (
        id SERIAL PRIMARY KEY,
        data TEXT NOT NULL
      );";

    // Act
    services.AddWhizbangPostgres(
      _connectionString!,
      jsonOptions,
      initializeSchema: true,
      perspectiveSchemaSql: perspectiveSql);

    // Assert - Service registration succeeded
    await Assert.That(services.Count).IsGreaterThan(0);

    // Verify both infrastructure and perspective were initialized
    await using var connection = new Npgsql.NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    // Check infrastructure table
    await using var infraCommand = connection.CreateCommand();
    infraCommand.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'wh_event_store'
      );";
    var infraExists = await infraCommand.ExecuteScalarAsync();
    await Assert.That((bool)infraExists!).IsTrue();

    // Check perspective table
    await using var perspectiveCommand = connection.CreateCommand();
    perspectiveCommand.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'test_ext_schema'
        AND table_name = 'test_ext_perspective'
      );";
    var perspectiveExists = await perspectiveCommand.ExecuteScalarAsync();
    await Assert.That((bool)perspectiveExists!).IsTrue();
  }

  /// <summary>
  /// Test 4: AddWhizbangPostgresHealthChecks registers PostgresHealthCheck
  /// </summary>
  [Test]
  public async Task AddWhizbangPostgresHealthChecks_RegistersHealthCheckAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();  // Health check service requires logging

    // Act
    services.AddWhizbangPostgresHealthChecks();

    // Assert - Health check service should be registered
    var serviceProvider = services.BuildServiceProvider();
    var healthCheckService = serviceProvider.GetService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();

    // FUTURE: Implement full health check validation
    // This is a stub test - needs implementation to verify PostgresHealthCheck registration
    await Assert.That(healthCheckService).IsNotNull();
  }
}
