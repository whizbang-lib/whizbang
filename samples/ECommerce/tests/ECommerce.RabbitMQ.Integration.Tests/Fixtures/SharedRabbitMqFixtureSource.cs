using Npgsql;
using Whizbang.Testing.Containers;

namespace ECommerce.RabbitMQ.Integration.Tests.Fixtures;

/// <summary>
/// Provides access to shared RabbitMQ and PostgreSQL containers from Whizbang.Testing.
/// Delegates to SharedRabbitMqContainer and SharedPostgresContainer for consistent
/// resource management across all test projects.
/// Tests run SEQUENTIALLY with per-test databases and separate hosts.
/// </summary>
public static class SharedRabbitMqFixtureSource {
  private static bool _initialized = false;

  /// <summary>
  /// Gets the shared RabbitMQ connection string.
  /// </summary>
  public static string RabbitMqConnectionString =>
    SharedRabbitMqContainer.ConnectionString;

  /// <summary>
  /// Gets the shared PostgreSQL connection string (base - tests create unique databases).
  /// </summary>
  public static string PostgresConnectionString =>
    SharedPostgresContainer.ConnectionString;

  /// <summary>
  /// Gets the RabbitMQ Management API URI.
  /// </summary>
  public static Uri ManagementApiUri =>
    SharedRabbitMqContainer.ManagementApiUri;

  /// <summary>
  /// Initializes shared RabbitMQ and PostgreSQL containers from Whizbang.Testing.
  /// Safe to call multiple times - delegates to singleton containers which have built-in health checks.
  /// </summary>
  public static async Task InitializeAsync(CancellationToken cancellationToken = default) {
    // ALWAYS call underlying containers - they handle health checks and re-initialization
    // Don't short-circuit here as containers may need to reinitialize if they died

    var wasInitialized = _initialized;

    if (!wasInitialized) {
      Console.WriteLine("================================================================================");
      Console.WriteLine("[SharedRabbitMqFixture] Initializing shared containers from Whizbang.Testing...");
      Console.WriteLine("================================================================================");
    }

    // Initialize both shared containers (they handle their own singleton pattern and health checks)
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    if (!wasInitialized) {
      Console.WriteLine("[SharedRabbitMqFixture] SharedPostgresContainer ready");
    }

    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);
    if (!wasInitialized) {
      Console.WriteLine("[SharedRabbitMqFixture] SharedRabbitMqContainer ready");
    }

    if (!wasInitialized) {
      Console.WriteLine("================================================================================");
      Console.WriteLine("[SharedRabbitMqFixture] Shared resources ready!");
      Console.WriteLine($"[SharedRabbitMqFixture] RabbitMQ: {RabbitMqConnectionString}");
      Console.WriteLine($"[SharedRabbitMqFixture] PostgreSQL: {PostgresConnectionString}");
      Console.WriteLine($"[SharedRabbitMqFixture] Management API: {ManagementApiUri}");
      Console.WriteLine("================================================================================");
    }

    _initialized = true;
  }

  /// <summary>
  /// Creates a unique database connection string for a specific test.
  /// Each test gets its own database for complete isolation.
  /// </summary>
  public static string GetPerTestDatabaseConnectionString() {
    if (!_initialized) {
      throw new InvalidOperationException("Fixture must be initialized before creating per-test databases");
    }

    // Generate unique database name using GUID
    var dbName = $"test_{Guid.NewGuid():N}";

    // Build connection string with unique database name
    // IMPORTANT: Use small pool sizes to avoid hitting PostgreSQL's max_connections limit
    var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString) {
      Database = dbName,
      MinPoolSize = 0,
      MaxPoolSize = 2,
      ConnectionIdleLifetime = 5,
      ConnectionPruningInterval = 3
    };

    return builder.ConnectionString;
  }

  /// <summary>
  /// Cleanup is now handled by the global shared containers in Whizbang.Testing.
  /// This method is kept for backward compatibility but does nothing.
  /// </summary>
  public static Task DisposeAsync() {
    // Containers are managed globally by Whizbang.Testing
    // Do NOT dispose them here - other test projects may still be using them
    _initialized = false;
    Console.WriteLine("[SharedRabbitMqFixture] Reset local state (containers managed globally)");
    return Task.CompletedTask;
  }
}
