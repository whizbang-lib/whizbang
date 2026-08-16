using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Health;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.RunControl;
using Whizbang.Data.EFCore.Postgres.Dispatch;
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
    /// <tests>Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_RegistersIClaimedEmissionStore_ScopedAsync</tests>
    /// <tests>Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_DoesNotOverrideExistingClaimedEmissionStore_Async</tests>
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

        // TURNKEY: the startup status surface's fleet section reads wh_service_instances through
        // this source. The surface itself is opt-in; registering the source merely makes the
        // fleet section answerable when a host mounts it.
        selector.Services.TryAddSingleton<Whizbang.Core.Startup.IStartupFleetStatusSource>(sp =>
            new EFCorePostgresStartupFleetStatusSource(
                sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                dbContextType));

        // TURNKEY: Register IClaimedEmissionStore so PublishOnceAsync (saga completion via
        // SagaCompletionGuard.EmitOnceAsync, idempotent receptor emissions, etc.) doesn't
        // throw at runtime. EFCoreClaimedEmissionStore writes ON CONFLICT DO NOTHING against
        // wh_unique_emission_claims through the consumer's DbContext. Without this, every
        // Postgres consumer had to wire it manually — observed in production where a saga
        // couldn't dispatch SagaCompletedEvent until the registration was added
        // (CompletionEventDispatched stayed false despite items being durably terminal).
        selector.Services.TryAddScoped<IClaimedEmissionStore>(sp =>
            new EFCoreClaimedEmissionStore(
                (Microsoft.EntityFrameworkCore.DbContext)sp.GetRequiredService(dbContextType)));

        // TURNKEY: Hosted service that runtime-registers RebuildPerspectiveCommandReceptor
        // with IReceptorRegistry at startup. Without this, dispatching RebuildPerspectiveCommand
        // has no effect — source-gen receptor discovery only sees the consumer's own syntax,
        // so a built-in receptor shipped from this assembly needs runtime registration.
        selector.Services.AddHostedService<RebuildCommandReceptorRegistrar>();

        // TURNKEY: A1 scheduled close — runtime-register ScheduledStreamCloseReceptor so a fired
        // "close the books" schedule occurrence drives IStreamCloser.CloseAsync. Same rationale as above.
        selector.Services.AddHostedService<ScheduledStreamCloseReceptorRegistrar>();

        // TURNKEY: stream-integrity R1b — runtime-register RedeliveryRequestReceptor so a received
        // RequestRedeliveryCommand drives the selection + targeted re-delivery pump. Same rationale as above.
        selector.Services.AddHostedService<RedeliveryRequestReceptorRegistrar>();

        // TURNKEY: #80-D scheduled integrity sweep — the receptor reacts to the idle-time cron's
        // occurrences, and the scheduler registers that cron on the temporal engine (standing the
        // audit worker's every-Nth-cycle counter down). Same rationale as above.
        selector.Services.AddHostedService<ScheduledIntegritySweepReceptorRegistrar>();
        selector.Services.AddHostedService<IntegritySweepScheduler>();

        // TURNKEY: stream-integrity B2/B3 — runtime-register IntegrityCheckpointReceptor so received
        // checkpoints drive windowed gap detection (and ladder-gated auto-repair). Same rationale as above.
        selector.Services.AddHostedService<IntegrityCheckpointReceptorRegistrar>();

        // TURNKEY: stream-integrity Phase A — runtime-register the manifest receptors so the deep
        // audit's request/response exchange works out of the box. Same rationale as above.
        selector.Services.AddHostedService<IntegrityManifestReceptorRegistrar>();

        // TURNKEY: Auto-initialize database schema. Workers wait on ISchemaReadyGate
        // (registered by AddWhizbangWorkers as a singleton) before issuing any SQL, so the
        // initializer's registration order in the IHostedService chain doesn't matter — even
        // if a worker's StartAsync runs first, it blocks on the gate until the initializer
        // calls MarkReady() at the end of migrations.
        selector.Services.TryAddSingleton<ISchemaInitializationRunner, DbContextSchemaInitializationRunner>();
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

        // TURNKEY: managed-resource health for the event-store DB. AlwaysRequired — a DB fault is a
        // real fault even during a migration (the migration needs it), so this replaces a consumer's
        // naive readiness check (no SELECT count(*) that a migration would make time out). The probe
        // is a lightweight SELECT 1 via the same NpgsqlDataSource; ConnectivityHealthSource combines
        // it with the lifecycle phase (Starting => not probed, Connecting => still warming, else probe).
        selector.Services.AddSingleton<IWhizbangHealthSource>(sp =>
          ConnectivityHealthSource.AlwaysRequired(
            "event-store",
            ct => _pingEventStoreAsync(sp.GetRequiredService<NpgsqlDataSource>(), ct),
            sp.GetRequiredService<IWhizbangLifecycleState>(),
            "event-store database unreachable"));

        // TURNKEY: Register table statistics provider + collector for OTel metrics.
        // The provider schema-qualifies its queries — pass the model's default schema so
        // multi-schema services report THEIR tables instead of probing a bare public schema.
        selector.Services.TryAddSingleton<ITableStatisticsProvider>(sp => {
          var ds = sp.GetRequiredService<NpgsqlDataSource>();
          using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
          var dbContext = (Microsoft.EntityFrameworkCore.DbContext)scope.ServiceProvider.GetRequiredService(dbContextType);
          var schema = dbContext.Model.GetDefaultSchema() ?? "public";
          return new PostgresTableStatisticsProvider(ds, schema);
        });
        selector.Services.TryAddSingleton<TableStatisticsMetrics>();

        // Durable stream-integrity convergence state. The in-memory ledger is per-process and
        // dies on restart, which is sound only while restarts are rare — but a report storm is
        // what causes the restarts, so every boot cleared the state that would have suppressed
        // it, and each replica reported the same divergence independently.
        selector.Services.TryAddSingleton<Whizbang.Core.Messaging.IIntegrityRepairLedger,
          CoordinatorIntegrityRepairLedger>();
        selector.Services.AddHostedService<TableStatisticsCollector>();

        // v0.502 DLQ — register IDeadLetterStore + IDeadLetterRecoveryService so the
        // dispatch worker can move failed inbox rows into wh_dead_letters and the
        // recovery worker can drain them back out. Without these the dispatch path's
        // _deadLetterStore field is null and the DLQ branch silently falls back to
        // mark-Published (observed in production: wh_dead_letters empty even with
        // WRN dead-letter logs firing).
        //
        // IDeadLetterStore is a SINGLETON (dispatch worker is singleton, can't inject
        // a scoped service). The adapter opens a fresh scope per MoveAsync so the
        // underlying EF Core path uses a properly-scoped DbContext / connection.
        //
        // IDeadLetterRecoveryService is SCOPED — DeadLetterRecoveryWorker explicitly
        // creates a scope per scan and resolves from it (see DeadLetterRecoveryWorker.cs).
        selector.Services.TryAddSingleton<IDeadLetterStore>(sp =>
          new ScopedEFCoreDeadLetterStore(
            sp.GetRequiredService<IServiceScopeFactory>(),
            dbContextType,
            sp.GetService<ILogger<EFCoreDeadLetterStore<Microsoft.EntityFrameworkCore.DbContext>>>()
              ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EFCoreDeadLetterStore<Microsoft.EntityFrameworkCore.DbContext>>.Instance,
            sp.GetService<WorkCoordinatorGate>()));
        selector.Services.TryAddScoped<IDeadLetterRecoveryService>(sp =>
          new EFCoreDeadLetterRecoveryService<Microsoft.EntityFrameworkCore.DbContext>(
            (Microsoft.EntityFrameworkCore.DbContext)sp.GetRequiredService(dbContextType),
            sp.GetService<ILogger<EFCoreDeadLetterRecoveryService<Microsoft.EntityFrameworkCore.DbContext>>>()
              ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EFCoreDeadLetterRecoveryService<Microsoft.EntityFrameworkCore.DbContext>>.Instance,
            sp.GetService<WorkCoordinatorGate>()));

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

        // TURNKEY: Apply the same connection-string-name convention to the
        // notification options that .WithDriver.Postgres already uses for EF
        // Core (derived from TDbContext via WithEFCore<T>(...) — e.g.,
        // AppServiceDbContext → "appservice-db"). The resolver will then try
        // ConnectionStrings:{name}-direct first (the bypass-pgbouncer variant
        // that some deployments already configure) and fall back to
        // ConnectionStrings:{name}. Operators only need to override this in
        // config when they want a different key per-environment.
        //
        // Late-binding via PostConfigure so an explicit
        // Whizbang:Database:ConnectionStringKey in appsettings still wins.
        var derivedConnectionStringName = selector.ConnectionStringName
          ?? _deriveConnectionStringName(selector.DbContextType.Name);
        if (!string.IsNullOrWhiteSpace(derivedConnectionStringName)) {
          selector.Services.PostConfigure<WhizbangNotificationOptions>(options => {
            if (string.IsNullOrWhiteSpace(options.ConnectionStringKey)) {
              options.ConnectionStringKey = derivedConnectionStringName;
            }
          });
        }

        return new WhizbangPerspectiveBuilder(selector.Services);
      }
    }
  }

  /// <summary>
  /// Thin shim around <see cref="Whizbang.Core.Naming.WhizbangNamingConvention.DeriveConnectionStringName"/>
  /// so existing call sites and tests don't need to change. The actual convention
  /// lives in <c>Whizbang.Core</c> so it can be referenced from any consumer code
  /// (and re-used by the source generator's regression test) without taking a
  /// runtime dependency on this assembly.
  /// </summary>
  internal static string _deriveConnectionStringName(string dbContextClassName)
    => Whizbang.Core.Naming.WhizbangNamingConvention.DeriveConnectionStringName(dbContextClassName);

  /// <summary>
  /// Lightweight event-store DB reachability probe: opens a pooled connection and runs <c>SELECT 1</c>.
  /// Throwing (connection refused, auth, timeout) is caught by <see cref="ConnectivityHealthSource"/>
  /// and read as unreachable. Deliberately trivial so it never queries a mid-migration table.
  /// </summary>
  private static async ValueTask<bool> _pingEventStoreAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken) {
    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT 1";
    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return true;
  }
}
