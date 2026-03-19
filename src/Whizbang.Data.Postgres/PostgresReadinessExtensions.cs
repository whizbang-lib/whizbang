using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Extension methods for registering PostgreSQL database readiness checks.
/// </summary>
/// <docs>data/postgres#readiness</docs>
public static class PostgresReadinessExtensions {
  /// <summary>
  /// Registers the PostgreSQL database readiness check.
  /// This is CRITICAL for ensuring workers don't start before the database schema is ready.
  /// Without this, workers may fail with "function does not exist" errors during startup.
  /// </summary>
  /// <param name="services">The service collection to register with.</param>
  /// <param name="connectionString">The PostgreSQL connection string.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <example>
  /// <code>
  /// // Register readiness check before workers start
  /// builder.Services.AddPostgresDatabaseReadiness(postgresConnection);
  ///
  /// // Now workers will wait for schema to be ready
  /// builder.Services.AddHostedService&lt;WorkCoordinatorPublisherWorker&gt;();
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresReadinessExtensionsTests.cs</tests>
  public static IServiceCollection AddPostgresDatabaseReadiness(
    this IServiceCollection services,
    string connectionString) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

    services.AddSingleton<IDatabaseReadinessCheck>(sp => {
      var logger = sp.GetRequiredService<ILogger<PostgresDatabaseReadinessCheck>>();
      return new PostgresDatabaseReadinessCheck(connectionString, logger);
    });

    return services;
  }

  /// <summary>
  /// Waits for the PostgreSQL database to be fully ready (connection and schema).
  /// Call this during application startup to ensure the database is ready before workers start.
  /// Uses configurable retry with exponential backoff.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="connectionString">The PostgreSQL connection string.</param>
  /// <param name="configureOptions">Optional configuration for retry settings.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <example>
  /// <code>
  /// // Wait for database with default settings (retry indefinitely until ready)
  /// builder.Services.WaitForPostgresReady(postgresConnection);
  ///
  /// // Wait for database with custom settings
  /// builder.Services.WaitForPostgresReady(postgresConnection, options => {
  ///   options.InitialRetryAttempts = 10;
  ///   options.MaxRetryDelay = TimeSpan.FromSeconds(60);
  /// });
  /// </code>
  /// </example>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Startup logging doesn't need high performance optimization")]
  public static IServiceCollection WaitForPostgresReady(
    this IServiceCollection services,
    string connectionString,
    Action<PostgresOptions>? configureOptions = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

    // Configure options
    var options = new PostgresOptions();
    configureOptions?.Invoke(options);

    // Build a temporary service provider to get logger
    var tempProvider = services.BuildServiceProvider();
    var logger = tempProvider.GetService<ILogger<PostgresConnectionRetry>>();

    // Wait for database to be ready
    logger?.LogInformation("Waiting for PostgreSQL database to be ready...");
    var connectionRetry = new PostgresConnectionRetry(options, logger);
    connectionRetry.WaitForDatabaseReadyAsync(connectionString).GetAwaiter().GetResult();
    logger?.LogInformation("PostgreSQL database ready");

    // Also register the readiness check for runtime monitoring
    return AddPostgresDatabaseReadiness(services, connectionString);
  }
}
