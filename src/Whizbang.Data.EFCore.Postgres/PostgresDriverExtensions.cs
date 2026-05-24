using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;

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
    /// Registers IPerspectiveStore&lt;T&gt;, ILensQuery&lt;T&gt;, IInbox, IOutbox, and IEventStore
    /// for all discovered perspective models via source-generated AOT-compatible code.
    /// Uses PostgresUpsertStrategy for native PostgreSQL ON CONFLICT support.
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
                (Microsoft.EntityFrameworkCore.DbContext)sp.GetRequiredService(dbContextType),
                sp.GetService<ILogger<EFCorePostgresPerspectiveCheckpointCompleter>>()));

        // TURNKEY: Hosted service that runtime-registers RebuildPerspectiveCommandReceptor
        // with IReceptorRegistry at startup. Without this, dispatching RebuildPerspectiveCommand
        // has no effect — source-gen receptor discovery only sees the consumer's own syntax,
        // so a built-in receptor shipped from this assembly needs runtime registration.
        selector.Services.AddHostedService<RebuildCommandReceptorRegistrar>();

        // TURNKEY: Auto-initialize database schema. Workers wait on ISchemaReadyGate
        // (registered by AddWhizbangWorkers as a singleton) before issuing any SQL, so the
        // initializer's registration order in the IHostedService chain doesn't matter — even
        // if a worker's StartAsync runs first, it blocks on the gate until the initializer
        // calls MarkReady() at the end of migrations.
        selector.Services.AddHostedService<WhizbangDatabaseInitializerService>();

        // Message type registry populator — reconciles wh_message_type_registry against the
        // compile-time IMessageTypeCatalog at startup. Uses NpgsqlDataSource directly so it
        // works without the Dapper driver being installed. WhizbangDatabaseInitializerService
        // invokes it after schema init.
        selector.Services.TryAddSingleton<IMessageTypeRegistryPopulator>(sp => {
          var catalog = sp.GetRequiredService<IMessageTypeCatalog>();
          var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
          var populatorLogger = sp.GetService<ILogger<EFCoreMessageTypeRegistryPopulator>>();
          return new EFCoreMessageTypeRegistryPopulator(catalog, dataSource, populatorLogger);
        });

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

        // TURNKEY: DbContext-backed fallback so the LISTEN/NOTIFY listener + commit-order
        // stamper + app-signal channel use the same connection string EF Core already has,
        // when neither Whizbang:Database:DirectConnectionString nor ConnectionStringKey is
        // configured. Operators only need the explicit notification config when they want a
        // bypass-pgbouncer "-direct" variant.
        selector.Services.TryAddSingleton<INotificationConnectionStringFallback>(sp =>
          new DbContextNotificationConnectionStringFallback(sp, dbContextType));

        // TURNKEY: Register Postgres LISTEN/NOTIFY listener. Binds WhizbangNotificationOptions
        // from "Whizbang:Database" so users only need to set ConnectionStringKey +
        // SignalingMode in appsettings; the listener resolves <key>-direct → <key> at startup.
        // SignalingMode.Polling makes the listener a no-op; SignalingMode.ListenNotify throws
        // at startup if no connection string can be resolved.
        selector.Services.AddWhizbangPostgresNotifications();

        return new WhizbangPerspectiveBuilder(selector.Services);
      }
    }
  }
}
