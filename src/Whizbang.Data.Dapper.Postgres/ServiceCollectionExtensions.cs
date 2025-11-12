using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Sequencing;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Extension methods for registering Whizbang PostgreSQL stores with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Registers all Whizbang PostgreSQL stores (EventStore, Inbox, Outbox, RequestResponseStore, SequenceProvider)
  /// along with the required database infrastructure (connection factory and executor).
  /// </summary>
  /// <param name="services">The service collection to register services with.</param>
  /// <param name="connectionString">The PostgreSQL connection string.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddWhizbangPostgres(this IServiceCollection services, string connectionString) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

    // Register database infrastructure
    services.AddSingleton<IDbConnectionFactory>(sp =>
      new PostgresConnectionFactory(connectionString));
    services.AddSingleton<IDbExecutor, DapperDbExecutor>();

    // Register Whizbang stores
    services.AddSingleton<IEventStore, DapperPostgresEventStore>();
    services.AddSingleton<IInbox, DapperPostgresInbox>();
    services.AddSingleton<IOutbox, DapperPostgresOutbox>();
    services.AddSingleton<IRequestResponseStore, DapperPostgresRequestResponseStore>();
    services.AddSingleton<ISequenceProvider, DapperPostgresSequenceProvider>();

    return services;
  }

  /// <summary>
  /// Registers health checks for PostgreSQL connectivity.
  /// Requires Microsoft.Extensions.Diagnostics.HealthChecks package.
  /// </summary>
  /// <param name="services">The service collection to register health checks with.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddWhizbangPostgresHealthChecks(this IServiceCollection services) {
    services.AddHealthChecks()
      .AddCheck<PostgresHealthCheck>("whizbang_postgres");

    return services;
  }
}
