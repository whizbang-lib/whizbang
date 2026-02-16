using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Extension property for selecting Postgres as the EF Core driver for Whizbang perspectives.
/// Uses C# 14 extension blocks to add .Postgres property to IDriverOptions.
/// Only visible when Whizbang.Data.EFCore.Postgres package is referenced.
/// </summary>
public static class PostgresDriverExtensions {
  /// <summary>
  /// Extension block for IDriverOptions.
  /// Adds .Postgres property for selecting PostgreSQL as the database driver.
  /// </summary>
  extension(IDriverOptions options) {
    /// <summary>
    /// Configures PostgreSQL as the database driver for EF Core perspectives.
    /// Registers IPerspectiveStore&lt;T&gt;, ILensQuery&lt;T&gt;, IInbox, IOutbox, IEventStore,
    /// and IDatabaseReadinessCheck for all discovered perspective models via source-generated AOT-compatible code.
    /// Uses PostgresUpsertStrategy for native PostgreSQL ON CONFLICT support.
    /// Automatically registers database readiness check for resilient worker startup.
    /// </summary>
    /// <returns>A WhizbangPerspectiveBuilder for further configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown if Postgres driver is used with non-EF Core storage.</exception>
    /// <example>
    /// <code>
    /// services
    ///     .AddWhizbangPerspectives()
    ///     .WithEFCore&lt;MyDbContext&gt;()
    ///     .WithDriver.Postgres;
    /// </code>
    /// </example>
    /// <tests>Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_WithValidEFCoreSelector_ReturnsWhizbangPerspectiveBuilderAsync</tests>
    /// <tests>Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_ReturnedBuilder_HasSameServicesAsync</tests>
    /// <tests>Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_WithNonEFCoreDriverOptions_ThrowsInvalidOperationExceptionAsync</tests>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S2325:Methods and properties that don't access instance data should be static", Justification = "C# 14 extension property - cannot be static. SonarCloud doesn't recognize extension member syntax.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Startup logging doesn't need high performance optimization")]
    public WhizbangPerspectiveBuilder Postgres {
      get {
        if (options is not EFCoreDriverSelector selector) {
          throw new InvalidOperationException(
              "Postgres driver can only be used with EF Core storage. " +
              "Call .WithEFCore<TDbContext>() before .WithDriver.Postgres");
        }

        // Invoke model registration callback (infrastructure + perspectives)
        // This is registered by source-generated module initializer in consumer assembly
        // The generated code contains AOT-safe registration using concrete types
        ModelRegistrationRegistry.InvokeRegistration(
            selector.Services,
            selector.DbContextType,
            new PostgresUpsertStrategy()
        );

        // Register IDatabaseReadinessCheck - CRITICAL for resilient worker startup
        // Extracts connection string from NpgsqlDataSource at resolution time
        // This ensures workers wait for database schema to be ready before processing
        selector.Services.TryAddSingleton<IDatabaseReadinessCheck>(sp => {
          var dataSource = sp.GetService<NpgsqlDataSource>();
          if (dataSource == null) {
            // Fallback: return default check that always returns true
            // User should register NpgsqlDataSource for proper readiness checking
            var fallbackLogger = sp.GetService<ILogger<DefaultDatabaseReadinessCheck>>();
            fallbackLogger?.LogWarning(
              "NpgsqlDataSource not registered. Database readiness check will always return true. " +
              "For proper startup resilience, register NpgsqlDataSource before AddDbContext.");
            return new DefaultDatabaseReadinessCheck();
          }

          var connectionString = dataSource.ConnectionString;
          var logger = sp.GetRequiredService<ILogger<PostgresDatabaseReadinessCheck>>();
          return new PostgresDatabaseReadinessCheck(connectionString, logger);
        });

        return new WhizbangPerspectiveBuilder(selector.Services);
      }
    }
  }
}
