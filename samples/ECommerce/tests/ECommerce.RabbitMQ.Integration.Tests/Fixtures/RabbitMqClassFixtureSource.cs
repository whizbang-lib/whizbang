using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace ECommerce.RabbitMQ.Integration.Tests.Fixtures;

/// <summary>
/// Per-class TestContainers fixture for RabbitMQ integration tests.
/// Provides isolated RabbitMQ and PostgreSQL containers for each test class.
/// Enables parallel test execution without interference.
/// </summary>
public sealed class RabbitMqClassFixtureSource : IDisposable {
  private readonly RabbitMqContainer _rabbitMqContainer;
  private readonly PostgreSqlContainer _postgresContainer;
  private bool _initialized;

  public RabbitMqClassFixtureSource() {
    // Create RabbitMQ container with management plugin enabled
    _rabbitMqContainer = new RabbitMqBuilder()
      .WithImage("rabbitmq:3.13-management-alpine")
      .WithUsername("guest")
      .WithPassword("guest")
      .WithPortBinding(15672, true)  // Expose Management API port
      .Build();

    // Create PostgreSQL container
    _postgresContainer = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("whizbang_test")
      .WithUsername("whizbang_user")
      .WithPassword("whizbang_pass")
      .Build();
  }

  /// <summary>
  /// RabbitMQ connection string (amqp://guest:guest@localhost:port)
  /// </summary>
  public string RabbitMqConnectionString { get; private set; } = "";

  /// <summary>
  /// PostgreSQL connection string
  /// </summary>
  public string PostgresConnectionString { get; private set; } = "";

  /// <summary>
  /// RabbitMQ Management API URI (http://localhost:15672)
  /// </summary>
  public Uri ManagementApiUri { get; private set; } = null!;

  /// <summary>
  /// Initializes the containers and retrieves connection strings.
  /// Starts both containers in parallel for faster setup (10-15s total).
  /// </summary>
  public async Task InitializeAsync() {
    if (_initialized) {
      return;
    }

    // Start containers in parallel (10-15s total)
    await Task.WhenAll(
      _rabbitMqContainer.StartAsync(),
      _postgresContainer.StartAsync()
    );

    RabbitMqConnectionString = _rabbitMqContainer.GetConnectionString();
    PostgresConnectionString = _postgresContainer.GetConnectionString();
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
    var builder = new Npgsql.NpgsqlConnectionStringBuilder(PostgresConnectionString) {
      Database = dbName
    };

    return builder.ConnectionString;
  }

  public void Dispose() {
    _rabbitMqContainer.DisposeAsync().AsTask().Wait();
    _postgresContainer.DisposeAsync().AsTask().Wait();
  }
}
