using Npgsql;
using Testcontainers.PostgreSql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Tests for PostgresSchemaInitializer to verify perspective schema integration.
/// Phase 1: PerspectiveSchemaGenerator Runtime Integration
/// Goal: 100% line and branch coverage
/// </summary>
public class PostgresSchemaInitializerTests : IAsyncDisposable {
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
  /// Test 1: Constructor accepts connection string without perspective SQL
  /// </summary>
  [Test]
  public async Task Constructor_WithConnectionStringOnly_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var initializer = new PostgresSchemaInitializer(_connectionString!);

    // Assert
    await Assert.That(initializer).IsNotNull();
  }

  /// <summary>
  /// Test 2: Constructor accepts connection string with perspective SQL
  /// </summary>
  [Test]
  public async Task Constructor_WithPerspectiveSql_InitializesSuccessfullyAsync() {
    // Arrange
    const string perspectiveSql = "CREATE TABLE IF NOT EXISTS test_perspective (id INT);";

    // Act
    var initializer = new PostgresSchemaInitializer(_connectionString!, perspectiveSql);

    // Assert
    await Assert.That(initializer).IsNotNull();
  }

  /// <summary>
  /// Test 3: InitializeSchemaAsync executes infrastructure SQL when no perspective SQL provided
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_NoPerspectiveSql_ExecutesInfrastructureOnlyAsync() {
    // Arrange
    var initializer = new PostgresSchemaInitializer(_connectionString!);

    // Act
    await initializer.InitializeSchemaAsync();

    // Assert - Verify Whizbang infrastructure tables exist
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'whizbang_event_store'
      );";

    var exists = await command.ExecuteScalarAsync();
    await Assert.That((bool)exists!).IsTrue();
  }

  /// <summary>
  /// Test 4: InitializeSchemaAsync executes both infrastructure and perspective SQL
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_WithPerspectiveSql_ExecutesBothSchemasAsync() {
    // Arrange
    const string perspectiveSql = @"
      CREATE SCHEMA IF NOT EXISTS test_schema;
      CREATE TABLE IF NOT EXISTS test_schema.test_perspective (
        id SERIAL PRIMARY KEY,
        name TEXT NOT NULL
      );";

    var initializer = new PostgresSchemaInitializer(_connectionString!, perspectiveSql);

    // Act
    await initializer.InitializeSchemaAsync();

    // Assert - Verify both infrastructure and perspective tables exist
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    // Check infrastructure table
    await using var infraCommand = connection.CreateCommand();
    infraCommand.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'whizbang_event_store'
      );";
    var infraExists = await infraCommand.ExecuteScalarAsync();
    await Assert.That((bool)infraExists!).IsTrue();

    // Check perspective table
    await using var perspectiveCommand = connection.CreateCommand();
    perspectiveCommand.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'test_schema'
        AND table_name = 'test_perspective'
      );";
    var perspectiveExists = await perspectiveCommand.ExecuteScalarAsync();
    await Assert.That((bool)perspectiveExists!).IsTrue();
  }

  /// <summary>
  /// Test 5: InitializeSchemaAsync skips perspective SQL when null
  /// Branch coverage: perspectiveSchemaSql == null
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_PerspectiveSqlNull_SkipsPerspectiveSqlAsync() {
    // Arrange
    var initializer = new PostgresSchemaInitializer(_connectionString!, perspectiveSchemaSql: null);

    // Act
    await initializer.InitializeSchemaAsync();

    // Assert - Only infrastructure exists (tested above), no exception thrown
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'whizbang_event_store'
      );";

    var exists = await command.ExecuteScalarAsync();
    await Assert.That((bool)exists!).IsTrue();
  }

  /// <summary>
  /// Test 6: InitializeSchemaAsync skips perspective SQL when empty string
  /// Branch coverage: perspectiveSchemaSql == ""
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_PerspectiveSqlEmpty_SkipsPerspectiveSqlAsync() {
    // Arrange
    var initializer = new PostgresSchemaInitializer(_connectionString!, perspectiveSchemaSql: "");

    // Act
    await initializer.InitializeSchemaAsync();

    // Assert - Only infrastructure exists, no exception thrown
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'whizbang_event_store'
      );";

    var exists = await command.ExecuteScalarAsync();
    await Assert.That((bool)exists!).IsTrue();
  }

  /// <summary>
  /// Test 7: InitializeSchema (sync) executes infrastructure SQL only
  /// </summary>
  [Test]
  public async Task InitializeSchema_NoPerspectiveSql_ExecutesInfrastructureOnlyAsync() {
    // Arrange
    var initializer = new PostgresSchemaInitializer(_connectionString!);

    // Act
    initializer.InitializeSchema(); // Synchronous method

    // Assert - Verify Whizbang infrastructure tables exist
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'whizbang_event_store'
      );";

    var exists = await command.ExecuteScalarAsync();
    await Assert.That((bool)exists!).IsTrue();
  }

  /// <summary>
  /// Test 8: InitializeSchema (sync) executes both infrastructure and perspective SQL
  /// </summary>
  [Test]
  public async Task InitializeSchema_WithPerspectiveSql_ExecutesBothSchemasAsync() {
    // Arrange
    const string perspectiveSql = @"
      CREATE SCHEMA IF NOT EXISTS test_schema_sync;
      CREATE TABLE IF NOT EXISTS test_schema_sync.test_perspective_sync (
        id SERIAL PRIMARY KEY,
        value TEXT NOT NULL
      );";

    var initializer = new PostgresSchemaInitializer(_connectionString!, perspectiveSql);

    // Act
    initializer.InitializeSchema(); // Synchronous method

    // Assert - Verify both infrastructure and perspective tables exist
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    // Check infrastructure table
    await using var infraCommand = connection.CreateCommand();
    infraCommand.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'whizbang_event_store'
      );";
    var infraExists = await infraCommand.ExecuteScalarAsync();
    await Assert.That((bool)infraExists!).IsTrue();

    // Check perspective table
    await using var perspectiveCommand = connection.CreateCommand();
    perspectiveCommand.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'test_schema_sync'
        AND table_name = 'test_perspective_sync'
      );";
    var perspectiveExists = await perspectiveCommand.ExecuteScalarAsync();
    await Assert.That((bool)perspectiveExists!).IsTrue();
  }
}
