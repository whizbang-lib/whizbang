using Npgsql;
using Whizbang.Testing.Containers;

namespace ECommerce.Lifecycle.Integration.Tests.Fixtures;

/// <summary>
/// Provides access to shared RabbitMQ and PostgreSQL containers from Whizbang.Testing.
/// </summary>
public static class SharedRabbitMqFixtureSource {
  private static bool _initialized;

  public static string RabbitMqConnectionString =>
    SharedRabbitMqContainer.ConnectionString;

  public static string PostgresConnectionString =>
    SharedPostgresContainer.ConnectionString;

  public static Uri ManagementApiUri =>
    SharedRabbitMqContainer.ManagementApiUri;

  public static async Task InitializeAsync(CancellationToken cancellationToken = default) {
    if (!_initialized) {
      Console.WriteLine("[LifecycleFixture] Initializing shared containers...");
    }

    await SharedPostgresContainer.InitializeOrSkipAsync(cancellationToken);
    await SharedRabbitMqContainer.InitializeOrSkipAsync(cancellationToken);

    if (!_initialized) {
      Console.WriteLine($"[LifecycleFixture] RabbitMQ: {RabbitMqConnectionString}");
      Console.WriteLine($"[LifecycleFixture] PostgreSQL: {PostgresConnectionString}");
      Console.WriteLine("[LifecycleFixture] Shared resources ready!");
    }

    _initialized = true;
  }

  public static string GetPerTestDatabaseConnectionString() {
    if (!_initialized) {
      throw new InvalidOperationException("Fixture must be initialized before creating per-test databases");
    }

    var dbName = $"test_{Guid.NewGuid():N}";
    var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString) {
      Database = dbName,
      MinPoolSize = 0,
      MaxPoolSize = 2,
      ConnectionIdleLifetime = 5,
      ConnectionPruningInterval = 3
    };

    return builder.ConnectionString;
  }
}
