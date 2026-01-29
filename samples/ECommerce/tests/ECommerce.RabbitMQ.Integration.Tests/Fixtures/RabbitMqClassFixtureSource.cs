using Dapper;
using Npgsql;
using Testcontainers.RabbitMq;
using Whizbang.Testing.Containers;

namespace ECommerce.RabbitMQ.Integration.Tests.Fixtures;

/// <summary>
/// Per-class TestContainers fixture for RabbitMQ integration tests.
/// Provides isolated RabbitMQ container and uses SharedPostgresContainer for PostgreSQL.
/// Enables parallel test execution without interference.
/// </summary>
public sealed class RabbitMqClassFixtureSource : IDisposable {
  private readonly RabbitMqContainer _rabbitMqContainer;
  private string? _fixtureDatabaseName;  // Unique database name for this fixture instance
  private string? _connectionString;  // Connection string pointing to the fixture's unique database
  private bool _initialized;

  public RabbitMqClassFixtureSource() {
    // Create RabbitMQ container with management plugin enabled
    _rabbitMqContainer = new RabbitMqBuilder()
      .WithImage("rabbitmq:3.13-management-alpine")
      .WithUsername("guest")
      .WithPassword("guest")
      .WithPortBinding(15672, true)  // Expose Management API port
      .Build();

    Console.WriteLine("[RabbitMqClassFixtureSource] Using SharedPostgresContainer for PostgreSQL");
  }

  /// <summary>
  /// RabbitMQ connection string (amqp://guest:guest@localhost:port)
  /// </summary>
  public string RabbitMqConnectionString { get; private set; } = "";

  /// <summary>
  /// PostgreSQL connection string
  /// </summary>
  public string PostgresConnectionString => _connectionString
    ?? throw new InvalidOperationException("Fixture must be initialized before accessing PostgresConnectionString");

  /// <summary>
  /// RabbitMQ Management API URI (http://localhost:15672)
  /// </summary>
  public Uri ManagementApiUri { get; private set; } = null!;

  /// <summary>
  /// Initializes the containers and retrieves connection strings.
  /// Starts RabbitMQ container and creates unique database in SharedPostgresContainer.
  /// </summary>
  public async Task InitializeAsync() {
    if (_initialized) {
      return;
    }

    // Initialize SharedPostgresContainer and start RabbitMQ container
    await SharedPostgresContainer.InitializeAsync();
    await _rabbitMqContainer.StartAsync();

    // Create a unique database for this fixture instance
    _fixtureDatabaseName = $"fixture_{Guid.NewGuid():N}";
    Console.WriteLine($"[RabbitMqClassFixtureSource] Creating unique database: {_fixtureDatabaseName}");

    await using (var conn = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await conn.OpenAsync();
      await conn.ExecuteAsync($"CREATE DATABASE \"{_fixtureDatabaseName}\"");
    }

    // Build connection string with the new database name
    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _fixtureDatabaseName
    };
    _connectionString = builder.ConnectionString;

    RabbitMqConnectionString = _rabbitMqContainer.GetConnectionString();
    ManagementApiUri = new Uri($"http://localhost:{_rabbitMqContainer.GetMappedPublicPort(15672)}");

    _initialized = true;
  }

  /// <summary>
  /// Creates a unique database connection string for a specific test.
  /// Each test gets its own database for complete isolation.
  /// </summary>
  public string GetPerTestDatabaseConnectionString() {
    if (!_initialized) {
      throw new InvalidOperationException("Fixture must be initialized before creating per-test databases");
    }

    // Generate unique database name using GUID (simple and reliable)
    // Format: test_<guid> (e.g., test_abc123..., ~37 characters total)
    var dbName = $"test_{Guid.NewGuid():N}";

    // Build connection string with unique database name
    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = dbName
    };

    return builder.ConnectionString;
  }

  public void Dispose() {
    _rabbitMqContainer.DisposeAsync().AsTask().Wait();

    // Drop the fixture's unique database from SharedPostgresContainer
    // The container itself is NOT disposed - it's shared across all tests
    if (_fixtureDatabaseName != null) {
      try {
        using var conn = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        conn.Open();
        // Terminate any remaining connections to the database before dropping
        conn.Execute($@"
          SELECT pg_terminate_backend(pid)
          FROM pg_stat_activity
          WHERE datname = '{_fixtureDatabaseName}' AND pid <> pg_backend_pid()");
        conn.Execute($"DROP DATABASE IF EXISTS \"{_fixtureDatabaseName}\"");
        Console.WriteLine($"[RabbitMqClassFixtureSource] Dropped database: {_fixtureDatabaseName}");
      } catch (Exception ex) {
        Console.WriteLine($"[RabbitMqClassFixtureSource] Warning: Failed to drop database {_fixtureDatabaseName}: {ex.Message}");
      }
    }
  }
}
