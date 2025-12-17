using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Tests for ServiceCollectionExtensions.AddWhizbangPostgres.
/// Phase 1: Verify perspective schema parameter integration.
/// </summary>
public class ServiceCollectionExtensionsTests : IAsyncDisposable {
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

    // TODO: Implement full health check validation
    // This is a stub test - needs implementation to verify PostgresHealthCheck registration
    await Assert.That(healthCheckService).IsNotNull();
  }
}
