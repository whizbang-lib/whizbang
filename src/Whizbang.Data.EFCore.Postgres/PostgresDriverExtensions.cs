using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
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

        // TURNKEY: Register DbContext and NpgsqlDataSource via generated callback
        // This is registered by source-generated module initializer in consumer assembly
        // Handles connection string resolution, JSON config, EnableDynamicJson(), and UseVector() if needed
        // The connection string name can be overridden via WithEFCore<T>("connection-string-name")
        DbContextRegistrationRegistry.InvokeRegistration(selector.Services, selector.DbContextType, selector.ConnectionStringName);

        // Invoke model registration callback (infrastructure + perspectives)
        // This is registered by source-generated module initializer in consumer assembly
        // The generated code contains AOT-safe registration using concrete types
        ModelRegistrationRegistry.InvokeRegistration(
            selector.Services,
            selector.DbContextType,
            new PostgresUpsertStrategy()
        );

        // TURNKEY: Wrap IEventStore with sync tracking decorator
        // This enables perspective synchronization by tracking emitted events
        // before they reach the database (cross-scope sync support)
        selector.Services.DecorateEventStoreWithSyncTracking();

        // TURNKEY: Register IPerspectiveCheckpointCompleter so PerspectiveRebuilder can
        // persist cursor checkpoints after rebuild. Without this, rebuild would still update
        // projection tables but wh_perspective_cursors would stay at whatever live processing
        // last wrote. Resolves the consumer's DbContext via the captured DbContextType.
        var dbContextType = selector.DbContextType;
        selector.Services.TryAddScoped<IPerspectiveCheckpointCompleter>(sp =>
            new EFCorePostgresPerspectiveCheckpointCompleter(
                (Microsoft.EntityFrameworkCore.DbContext)sp.GetRequiredService(dbContextType)));

        // TURNKEY: Hosted service that runtime-registers RebuildPerspectiveCommandReceptor
        // with IReceptorRegistry at startup. Without this, dispatching RebuildPerspectiveCommand
        // has no effect — source-gen receptor discovery only sees the consumer's own syntax,
        // so a built-in receptor shipped from this assembly needs runtime registration.
        selector.Services.AddHostedService<RebuildCommandReceptorRegistrar>();

        // TURNKEY: Auto-initialize database schema before workers start
        // Registered before PerspectiveWorker to ensure StartAsync ordering
        selector.Services.AddHostedService<WhizbangDatabaseInitializerService>();

        // TURNKEY: Invoke perspective runner registration callbacks
        // This is registered by source-generated module initializer in consumer assembly
        // Automatically registers IPerspectiveRunnerRegistry, all runners, and PerspectiveWorker
        PerspectiveRunnerCallbackRegistry.InvokeRegistration(selector.Services);

        // TURNKEY: Register perspective snapshot and rewind options
        selector.Services.AddOptions<PerspectiveSnapshotOptions>();
        selector.Services.AddOptions<PerspectiveRewindOptions>();

        // TURNKEY: Register perspective snapshot store for efficient rewind
        // Uses NpgsqlDataSource for connection management (same as readiness check)
        selector.Services.TryAddSingleton<IPerspectiveSnapshotStore>(sp => {
          var ds = sp.GetRequiredService<NpgsqlDataSource>();
          var snapshotLogger = sp.GetService<ILogger<EFCorePerspectiveSnapshotStore>>();
          return new EFCorePerspectiveSnapshotStore(ds, snapshotLogger);
        });

        // TURNKEY: Register table statistics provider + collector for OTel metrics
        selector.Services.TryAddSingleton<ITableStatisticsProvider>(sp => {
          var ds = sp.GetRequiredService<NpgsqlDataSource>();
          return new PostgresTableStatisticsProvider(ds);
        });
        selector.Services.TryAddSingleton<TableStatisticsMetrics>();
        selector.Services.AddHostedService<TableStatisticsCollector>();

        // Register IDatabaseReadinessCheck - CRITICAL for resilient worker startup
        // Uses NpgsqlDataSource directly to create connections (avoids password stripping bug)
        // This ensures workers wait for database schema to be ready before processing
        selector.Services.TryAddSingleton<IDatabaseReadinessCheck>(sp => {
          var dataSource = sp.GetService<NpgsqlDataSource>();
          if (dataSource == null) {
            // Fallback: return default check that always returns true
            // User should register NpgsqlDataSource for proper readiness checking
            var fallbackLogger = sp.GetService<ILogger<DefaultDatabaseReadinessCheck>>();
#pragma warning disable CA1848 // Use the LoggerMessage delegates - startup logging doesn't need high performance
            fallbackLogger?.LogWarning(
              "NpgsqlDataSource not registered. Database readiness check will always return true. " +
              "For proper startup resilience, register NpgsqlDataSource before AddDbContext.");
#pragma warning restore CA1848
            return new DefaultDatabaseReadinessCheck();
          }

          // IMPORTANT: Pass NpgsqlDataSource directly instead of extracting ConnectionString.
          // NpgsqlDataSource.ConnectionString strips passwords for security, causing auth failures.
          // DataSource.CreateConnection() properly retains credentials internally.
          var logger = sp.GetRequiredService<ILogger<PostgresDatabaseReadinessCheck>>();
          return new PostgresDatabaseReadinessCheck(dataSource, logger);
        });

        return new WhizbangPerspectiveBuilder(selector.Services);
      }
    }
  }
}
