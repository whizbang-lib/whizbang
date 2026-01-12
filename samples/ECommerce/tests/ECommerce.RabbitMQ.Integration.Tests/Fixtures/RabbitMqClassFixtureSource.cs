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
    if (_initialized) return;

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

  public void Dispose() {
    _rabbitMqContainer.DisposeAsync().AsTask().Wait();
    _postgresContainer.DisposeAsync().AsTask().Wait();
  }
}
