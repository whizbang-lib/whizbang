using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Notifications.AppSignals;
using Whizbang.Core.Signals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// DI registration for the Postgres LISTEN/NOTIFY work-signal listener.
/// </summary>
/// <remarks>
/// Auto-invoked by <c>.WithDriver.Postgres</c>; consumers don't need to call this directly.
/// Idempotent — calling multiple times has no additional effect.
///
/// Configuration is bound from the <c>Whizbang:Database</c> section so consumers can
/// bake the convention into appsettings + environment variables and never have to think
/// about wiring:
///
/// <code>
/// {
///   "Whizbang": {
///     "Database": {
///       "ConnectionStringKey": "app-db",
///       "SignalingMode": "Auto"
///     }
///   }
/// }
/// </code>
///
/// At startup, <see cref="PgWorkNotificationListener"/> resolves the connection string per
/// the precedence in <see cref="NotificationConnectionStringResolver"/>:
/// <list type="number">
///   <item><description>Explicit <see cref="WhizbangNotificationOptions.DirectConnectionString"/></description></item>
///   <item><description><c>ConnectionStrings:{ConnectionStringKey}-direct</c></description></item>
///   <item><description><c>ConnectionStrings:{ConnectionStringKey}</c> (pooled fallback)</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public static class PostgresNotificationsServiceCollectionExtensions {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>Configuration section the listener binds <see cref="WhizbangNotificationOptions"/> from.</summary>
  public const string CONFIGURATION_SECTION = "Whizbang:Database";
#pragma warning restore CA1707

  /// <summary>
  /// Registers the Postgres LISTEN/NOTIFY listener and binds
  /// <see cref="WhizbangNotificationOptions"/> from the <c>Whizbang:Database</c>
  /// configuration section. Replaces the default <see cref="NoOpWorkNotificationListener"/>
  /// from <c>AddWhizbangWorkers</c>.
  /// </summary>
  /// <remarks>
  /// Notification data source auto-discovery, first hit wins: explicit <c>Whizbang:Database</c>
  /// options; a credential-bearing <c>ConnectionStrings</c> entry; the application's own data source
  /// via <see cref="INotificationDataSourceFallback"/> (borrowed); an <see cref="NpgsqlDataSource"/>
  /// registered in DI (borrowed); otherwise the string path with its startup diagnostic.
  /// </remarks>
  /// <docs>data/drivers#bring-your-own-dbcontext</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/NotificationDataSourceAutoDiscoveryTests.cs</tests>
  /// <tests>tests/Whizbang.Core.Tests/Notifications/AddWhizbangPostgresNotificationsTests.cs</tests>
  public static IServiceCollection AddWhizbangPostgresNotifications(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    // AOT-safe options binding: register an IConfigureOptions impl that reads IConfiguration
    // values manually instead of using the reflection-based BindConfiguration<TOptions>.
    services.AddOptions<WhizbangNotificationOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      IConfigureOptions<WhizbangNotificationOptions>,
      ConfigureWhizbangNotificationOptionsFromConfiguration>());

    // NotifyMetrics: counters + UpDownCounter for the LISTEN/NOTIFY work-signaling path.
    // Registered as singleton so the same instances are observed across the connection
    // singleton + every per-channel listener.
    // NotifyMetrics depends on WhizbangMetrics. TryAddSingleton it here so callers that
    // AddWhizbangPostgresNotifications without first calling AddWhizbangCore (e.g. some
    // narrow unit-tests) still resolve cleanly. No-op when AddWhizbangCore ran first.
    services.TryAddSingleton<Whizbang.Core.Observability.WhizbangMetrics>();
    services.TryAddSingleton<NotifyMetrics>();

    // Slice 33 — shared direct connection: ONE NpgsqlConnection per pod multiplexes
    // every per-channel subscription. PgSharedNotifyConnection is the singleton that
    // implements both INotifySignalingGate (killswitch + functionality probe) and
    // ISharedNotifyConnection (subscription registry + dispatch).
    services.TryAddSingleton<PgSharedNotifyConnection>();
    services.AddSingleton<INotifySignalingGate>(sp => sp.GetRequiredService<PgSharedNotifyConnection>());
    services.AddSingleton<ISharedNotifyConnection>(sp => sp.GetRequiredService<PgSharedNotifyConnection>());
    services.AddHostedService(sp => sp.GetRequiredService<PgSharedNotifyConnection>());

    // Replace the NoOp listener registered by AddWhizbangWorkers with the real one — but
    // the listener is now a thin subscriber. It subscribes via the shared connection in
    // its IHostedService.StartAsync.
    services.RemoveAll<IWorkNotificationListener>();
    services.TryAddSingleton<PgWorkNotificationListener>();
    services.AddSingleton<IWorkNotificationListener>(sp => sp.GetRequiredService<PgWorkNotificationListener>());
    services.AddHostedService(sp => sp.GetRequiredService<PgWorkNotificationListener>());

    // Slice 26.12: register the commit-order stamper worker. Singleton per pod via
    // pg_try_advisory_lock — every instance hosts it, only the lock-holder actively stamps.
    // Bound to CommitOrderStamperOptions via the same Whizbang:Database section.
    services.AddOptions<CommitOrderStamperOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      IConfigureOptions<CommitOrderStamperOptions>,
      ConfigureCommitOrderStamperOptionsFromConfiguration>());
    // Register as singleton AND as a hosted service so the worker is also
    // resolvable directly (tests and diagnostics rely on this).
    services.TryAddSingleton<PgCommitOrderStamperWorker>();
    services.AddHostedService(sp => sp.GetRequiredService<PgCommitOrderStamperWorker>());

    // App-signal channel (publishes pg_notify on wh_app_<topic>).
    services.TryAddSingleton<IAppSignalChannel, PgAppSignalChannel>();

    // System Signal Bus — ensure the bus itself is registered before we add the Postgres transport
    // and the hosted services that DEPEND on it (PgDurableSignalTailWorker needs ISignalSink;
    // PgInstanceLifecycleMonitor and ScheduleWorker need ISignalBus). AddWhizbang wires the bus for
    // full hosts, but this extension is also used standalone — without this call those hosted
    // services can't be constructed and GetServices<IHostedService>() throws. AddWhizbangSignalBus
    // is fully idempotent (TryAdd throughout), so calling it here is safe when the bus is already
    // registered.
    services.AddWhizbangSignalBus();

    // The Postgres NOTIFY push transport, registered alongside the in-memory transport that
    // AddWhizbangSignalBus registers by default. Both transports fan out from the same SignalBus so
    // callers get NOTIFY-backed cross-process delivery plus in-process loopback for tests.
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      Whizbang.Core.Signals.ISignalTransport, PostgresSignalTransport>());

    // Signal-bus pull sources — periodic reconciliation backstops for the three work-available
    // signal types. They run alongside NOTIFY: when the push transport is healthy the poll adds
    // latency-relaxed correctness checks; when NOTIFY is unavailable they carry the wake load.
    // TimeProvider is resolved from DI so tests can inject a FakeTimeProvider.
    services.TryAddSingleton<TimeProvider>(sp => TimeProvider.System);
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      Whizbang.Core.Signals.ISignalSource, PgOutboxWorkAvailablePollSource>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      Whizbang.Core.Signals.ISignalSource, PgInboxWorkAvailablePollSource>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      Whizbang.Core.Signals.ISignalSource, PgPerspectiveWorkAvailablePollSource>());
    // Temporal engine (F2): backstop pull source for due schedules.
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      Whizbang.Core.Signals.ISignalSource, PgScheduleDuePollSource>());
    // Temporal engine (F2): the authoritative claimer (wh_claim_due_schedules) + the firing worker.
    // ScheduleWorker wakes on the ScheduleDueSignal doorbell (poll source / arm-on-mutation NOTIFY)
    // and reconciles on its backstop interval, draining due schedules through the claimer.
    services.TryAddSingleton<Whizbang.Core.Temporal.IScheduleClaimer, PgScheduleClaimer>();
    services.AddHostedService<Whizbang.Core.Temporal.ScheduleWorker>();
    // Temporal engine (F2): the schedule management API (create / pause / resume / cancel / trigger / update).
    services.TryAddSingleton<Whizbang.Core.Temporal.IScheduleManager, PgScheduleManager>();
    // Saga deadlines ride the same engine — a deadline is a keyed one-shot schedule.
    services.TryAddSingleton<Whizbang.Core.Temporal.ISagaDeadlineScheduler, Whizbang.Core.Temporal.SagaDeadlineScheduler>();
    // Pre-fire gate (F2 increment 6b): runs the developer's IScheduleFireHook right before a scheduled
    // occurrence executes. Inert unless the developer registers an IScheduleFireHook — with none, the gate
    // proceeds for everything and publishing is byte-for-byte unchanged.
    services.TryAddSingleton<Whizbang.Core.Temporal.IScheduleOccurrenceStore, PgScheduleOccurrenceStore>();
    services.TryAddSingleton<Whizbang.Core.Workers.IOccurrencePublishGate,
      Whizbang.Core.Temporal.ScheduleOccurrencePublishGate>();

    // Durable-signal tail worker — delivers Delivery=Durable signals persisted to wh_signals
    // on a per-instance cursor. Must-not-miss signals (e.g. InstanceDied → orphan takeover)
    // ride this tail as insurance against NOTIFY drops.
    services.TryAddSingleton<PgDurableSignalTailWorker>();
    services.AddHostedService(sp => sp.GetRequiredService<PgDurableSignalTailWorker>());

    // Instance lifecycle monitor — scans wh_service_instances for stale heartbeats and
    // publishes the durable InstanceDiedSignal so live pods drive orphan takeover.
    services.TryAddSingleton<PgInstanceLifecycleMonitor>();
    services.AddHostedService(sp => sp.GetRequiredService<PgInstanceLifecycleMonitor>());

    // Retention sweep for wh_signals — deletes rows older than the retention window that every
    // pod's tail has already consumed. Runs hourly; a stalled tail keeps its rows alive because
    // the delete is gated by MIN(last_delivered_signal_id) across all cursors.
    services.TryAddSingleton<PgDurableSignalRetentionWorker>();
    services.AddHostedService(sp => sp.GetRequiredService<PgDurableSignalRetentionWorker>());

    // Duty election (startup-pipeline increment 7): duties are won on a session advisory lock over
    // a dedicated direct connection, with holdings recorded via record_capability — the lock
    // decides, the row reports.
    services.TryAddSingleton<Whizbang.Core.Startup.IDutyElector, PgDutyElector>();

    // Default-on auto-discovery: when no INotificationDataSource has been
    // explicitly registered (the caller didn't call
    // AddWhizbangNotificationDataSource), and no explicit
    // WhizbangNotificationOptions.{DirectConnectionString,ConnectionStringKey}
    // is set, walk IConfiguration:ConnectionStrings for the first
    // credential-bearing entry and build a dedicated data source from it.
    // This makes the SCRAM-SHA-256 / UseNpgsql(NpgsqlDataSource) failure
    // mode fix work out of the box — consumers don't have to know about it.
    services.TryAddSingleton<INotificationDataSource>(sp => {
      var configuration = sp.GetRequiredService<IConfiguration>();
      var options = sp.GetService<IOptions<WhizbangNotificationOptions>>()?.Value
        ?? new WhizbangNotificationOptions();

      // If the explicit-options path will resolve a credential-bearing string,
      // the workers can use it directly — no auto-discovery needed. Same
      // precedence as NotificationConnectionStringResolver.
      var resolution = NotificationConnectionStringResolver.Resolve(options, configuration, fallback: null);
      var connectionString = resolution.ConnectionString;
      if (string.IsNullOrEmpty(connectionString)) {
        connectionString = _findFirstCredentialBearingConnectionString(configuration);
      }
      if (string.IsNullOrEmpty(connectionString)) {
        // No credential in configuration. Under UseNpgsql(NpgsqlDataSource) the string path
        // cannot work either — Npgsql redacts the password from every ConnectionString surface —
        // but the application's own data source still holds the credentials. Borrow it: first the
        // DbContext's actual data source (surfaced by the storage driver), then one registered in
        // DI. Borrowed means never disposed here and used as-is. Reported once, so a later auth
        // failure on a LISTEN connection can be read against which source was chosen.
        var borrowed = sp.GetService<INotificationDataSourceFallback>()?.GetDataSource()
          ?? sp.GetService<NpgsqlDataSource>();
        if (borrowed is not null) {
          var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Data.Postgres.Notifications");
          if (logger is not null) {
            NotificationDataSourceDiscoveryLog.ReusingApplicationDataSource(logger);
          }
          return new NotificationDataSource(borrowed, ownsDataSource: false);
        }
        // Nothing usable found — return a wrapper with DataSource=null so the
        // workers fall back to their string-based path AND surface the
        // operator-actionable startup diagnostic.
        return new NotificationDataSource(dataSource: null, ownsDataSource: false);
      }
      var builder = new NpgsqlDataSourceBuilder(connectionString);
      // Multi-schema deployments: the shared LISTEN connection also issues table/function
      // SQL (alive-lock claim, resync probes) that lives in the SERVICE schema. Apply the
      // resolved search path unless the connection string already carries one.
      _applySearchPath(builder, options, sp);
      // Pool sizing for notification workers:
      //   - PgSharedNotifyConnection holds one long-lived LISTEN connection
      //   - PgCommitOrderStamperWorker holds one long-lived advisory-lock connection
      //   - Both also issue short-lived stamp + probe + LISTEN-resync commands
      // 8 gives the long-held pair plus headroom for the burst connections under
      // contention. A smaller pool (we tried 4) starved test runs that drove
      // multiple parallel hosts.
      builder.ConnectionStringBuilder.MaxPoolSize = 8;
      // Slice 2 of zero-idle-polling — stamp application_name with the per-pod
      // identity so pg_stat_activity rows are distinguishable per-pod. The
      // wh_live_instances view (migration 052) joins on this exact format.
      var instanceProvider = sp.GetRequiredService<Whizbang.Core.Observability.IServiceInstanceProvider>();
      builder.ConnectionStringBuilder.ApplicationName = PgSharedNotifyConnection.ComputeApplicationName(instanceProvider.InstanceId);
      // Slice 3 of zero-idle-polling — TCP keepalive params so the kernel detects
      // silent NAT/firewall connection death within ~150 s instead of the OS
      // default (~2 hours on Linux). The gate's reprobe loop then flips
      // IsAvailable to false and dependent workers engage their backstop poll.
      ApplyTcpKeepAlive(builder.ConnectionStringBuilder, options);
      // ownsDataSource: true means the wrapper's DI-driven disposal will dispose
      // the data source. Critical for tests that spin many hosts in sequence —
      // without this, every host leaks an Npgsql connection pool.
      return new NotificationDataSource(builder.Build(), ownsDataSource: true);
    });

    return services;
  }

  /// <summary>
  /// Applies the per-options TCP keepalive settings to an
  /// <see cref="NpgsqlConnectionStringBuilder"/>. Pure mutation — no DI, no side effects
  /// beyond the builder. Slice 3 of zero-idle-polling.
  /// </summary>
  /// <remarks>
  /// Called from both the auto-discovery data-source factory and the explicit
  /// <see cref="AddWhizbangNotificationDataSource"/> path so notification connections
  /// detect silent NAT-style death within
  /// <c>TcpKeepAliveTime + N × TcpKeepAliveInterval</c> seconds instead of the OS default
  /// (typically ~2 hours). The gate's reprobe loop then notices the failure and flips
  /// <c>IsAvailable</c> to false, which in turn triggers the backstop poll path on
  /// dependent workers.
  /// </remarks>
  internal static void ApplyTcpKeepAlive(
      NpgsqlConnectionStringBuilder builder,
      WhizbangNotificationOptions options) {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(options);
    builder.TcpKeepAlive = true;
    builder.TcpKeepAliveTime = options.TcpKeepAliveTime;
    builder.TcpKeepAliveInterval = options.TcpKeepAliveInterval;
  }

  /// <summary>
  /// Walks <see cref="IConfiguration"/>'s <c>ConnectionStrings</c> section for
  /// the first entry whose string carries a Postgres credential marker.
  /// Returns null when none qualify.
  /// </summary>
  private static string? _findFirstCredentialBearingConnectionString(IConfiguration configuration) {
    foreach (var child in configuration.GetSection("ConnectionStrings").GetChildren()) {
      var value = child.Value;
      if (string.IsNullOrEmpty(value)) {
        continue;
      }
      var (_, hasSecret) = ConnectionStringCredentialMarkerSummary.Summarize(value);
      if (hasSecret) {
        return value;
      }
    }
    return null;
  }


  /// <summary>
  /// Registers a dedicated <see cref="NpgsqlDataSource"/> for the notification
  /// workers and wires it through <see cref="INotificationDataSource"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Some production deployments configure their EF Core DbContext via
  /// <c>UseNpgsql(NpgsqlDataSource)</c>. In that configuration Npgsql strips
  /// credentials from every public ConnectionString surface — including
  /// <c>NpgsqlDataSource.ConnectionString</c> — and there is no string-based
  /// recovery path. The notification workers <em>must</em> hold a real
  /// data source they can ask to open authenticated connections.
  /// </para>
  /// <para>
  /// This helper builds an <strong>independent</strong> data source for
  /// notifications (small pool, sized for the LISTEN/NOTIFY + leader-lock
  /// workload) so the notification workers don't compete with EF Core's
  /// connection pool. Calling this is the single line of glue a consumer needs:
  /// </para>
  /// <code>
  /// services.AddWhizbangPostgresNotifications();
  /// services.AddWhizbangNotificationDataSource(connectionString);
  /// </code>
  /// </remarks>
  /// <param name="services">The service collection.</param>
  /// <param name="connectionString">
  /// The credential-bearing connection string the data source should use.
  /// Typically read from <c>IConfiguration.GetConnectionString(...)</c>.
  /// </param>
  /// <returns>The service collection for chaining.</returns>
  /// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
  public static IServiceCollection AddWhizbangNotificationDataSource(
      this IServiceCollection services,
      string connectionString) {
    ArgumentNullException.ThrowIfNull(services);
    if (string.IsNullOrWhiteSpace(connectionString)) {
      throw new ArgumentException("connectionString must be non-empty", nameof(connectionString));
    }

    services.AddSingleton<INotificationDataSource>(sp => {
      // Independent data source — its pool is NOT shared with EF Core.
      // 4 connections is plenty: one for the LISTEN/NOTIFY shared connection,
      // one for the commit-order stamper's advisory-lock holder, plus headroom
      // for the leader-election retry and probe paths.
      var builder = new NpgsqlDataSourceBuilder(connectionString);
      var explicitOptions = sp.GetService<Microsoft.Extensions.Options.IOptions<WhizbangNotificationOptions>>()?.Value
        ?? new WhizbangNotificationOptions();
      _applySearchPath(builder, explicitOptions, sp);
      // Pool sizing for notification workers:
      //   - PgSharedNotifyConnection holds one long-lived LISTEN connection
      //   - PgCommitOrderStamperWorker holds one long-lived advisory-lock connection
      //   - Both also issue short-lived stamp + probe + LISTEN-resync commands
      // 8 gives the long-held pair plus headroom for the burst connections under
      // contention. A smaller pool (we tried 4) starved test runs that drove
      // multiple parallel hosts.
      builder.ConnectionStringBuilder.MaxPoolSize = 8;
      // Slice 2 of zero-idle-polling — stamp application_name with the per-pod
      // identity so pg_stat_activity rows are distinguishable per-pod. The
      // wh_live_instances view (migration 052) joins on this exact format.
      var instanceProvider = sp.GetRequiredService<Whizbang.Core.Observability.IServiceInstanceProvider>();
      builder.ConnectionStringBuilder.ApplicationName = PgSharedNotifyConnection.ComputeApplicationName(instanceProvider.InstanceId);
      // Slice 3 of zero-idle-polling — TCP keepalive params (same defaults as
      // the auto-discovery path).
      var options = sp.GetService<IOptions<WhizbangNotificationOptions>>()?.Value
        ?? new WhizbangNotificationOptions();
      ApplyTcpKeepAlive(builder.ConnectionStringBuilder, options);
      return new NotificationDataSource(builder.Build());
    });

    return services;
  }

  /// <summary>
  /// Applies the resolved search path (explicit <see cref="WhizbangNotificationOptions.SearchPath"/>,
  /// else the registered <see cref="Whizbang.Core.Notifications.INotificationConnectionStringFallback"/>'s
  /// schema) to the data source builder — unless the connection string already carries one.
  /// </summary>
  private static void _applySearchPath(NpgsqlDataSourceBuilder builder, WhizbangNotificationOptions options, IServiceProvider sp) {
    var fallback = sp.GetService<Whizbang.Core.Notifications.INotificationConnectionStringFallback>();
    var searchPath = !string.IsNullOrWhiteSpace(options.SearchPath) ? options.SearchPath : fallback?.GetSearchPath();
    if (!string.IsNullOrWhiteSpace(searchPath) && string.IsNullOrWhiteSpace(builder.ConnectionStringBuilder.SearchPath)) {
      builder.ConnectionStringBuilder.SearchPath = searchPath;
    }
  }
}

/// <summary>
/// AOT-safe <see cref="IConfigureOptions{TOptions}"/> for <see cref="CommitOrderStamperOptions"/>.
/// Reads <c>Whizbang:Database:Stamper</c> sub-section so operators can override defaults
/// without touching the rest of the notification settings.
/// </summary>
internal sealed class ConfigureCommitOrderStamperOptionsFromConfiguration(IConfiguration configuration)
  : IConfigureOptions<CommitOrderStamperOptions> {

  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

  public void Configure(CommitOrderStamperOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var section = _configuration.GetSection(PostgresNotificationsServiceCollectionExtensions.CONFIGURATION_SECTION + ":Stamper");
    if (!section.Exists()) {
      return;
    }

    if (TimeSpan.TryParse(section["PollingInterval"], System.Globalization.CultureInfo.InvariantCulture, out var poll)) {
      options.PollingInterval = poll;
    }

    if (TimeSpan.TryParse(section["LeaderElectionRetry"], System.Globalization.CultureInfo.InvariantCulture, out var retry)) {
      options.LeaderElectionRetry = retry;
    }

    if (int.TryParse(section["BatchSize"], System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out var batch)) {
      options.BatchSize = batch;
    }

    if (bool.TryParse(section["DisableStamper"], out var disable)) {
      options.DisableStamper = disable;
    }

    if (long.TryParse(section["AdvisoryLockKey"], System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out var lockKey)) {
      options.AdvisoryLockKey = lockKey;
    }
  }
}

/// <summary>
/// AOT-safe <see cref="IConfigureOptions{TOptions}"/> for <see cref="WhizbangNotificationOptions"/>.
/// Reads <c>Whizbang:Database</c> values from <see cref="IConfiguration"/> manually so we
/// don't take a dependency on the reflection-based options binder (avoids IL2026 / IL3050).
/// </summary>
internal sealed class ConfigureWhizbangNotificationOptionsFromConfiguration(IConfiguration configuration)
  : IConfigureOptions<WhizbangNotificationOptions> {

  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

  public void Configure(WhizbangNotificationOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var section = _configuration.GetSection(PostgresNotificationsServiceCollectionExtensions.CONFIGURATION_SECTION);
    if (!section.Exists()) {
      return;
    }

    var modeRaw = section["SignalingMode"];
    if (!string.IsNullOrWhiteSpace(modeRaw)
        && Enum.TryParse<WorkSignalingMode>(modeRaw, ignoreCase: true, out var mode)) {
      options.SignalingMode = mode;
    }

    var key = section["ConnectionStringKey"];
    if (!string.IsNullOrWhiteSpace(key)) {
      options.ConnectionStringKey = key;
    }

    var direct = section["DirectConnectionString"];
    if (!string.IsNullOrWhiteSpace(direct)) {
      options.DirectConnectionString = direct;
    }

    if (bool.TryParse(section["DisableNotifications"], out var disable)) {
      options.DisableNotifications = disable;
    }

    if (TimeSpan.TryParse(section["PollingFallbackInterval"], out var pollFallback)) {
      options.PollingFallbackInterval = pollFallback;
    }

    if (TimeSpan.TryParse(section["ListenKeepaliveInterval"], out var keepalive)) {
      options.ListenKeepaliveInterval = keepalive;
    }

    if (TimeSpan.TryParse(section["ListenReconnectInitialDelay"], out var initialDelay)) {
      options.ListenReconnectInitialDelay = initialDelay;
    }

    if (TimeSpan.TryParse(section["ListenReconnectMaxDelay"], out var maxDelay)) {
      options.ListenReconnectMaxDelay = maxDelay;
    }

    if (double.TryParse(section["ListenReconnectBackoffMultiplier"],
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,
        out var multiplier)) {
      options.ListenReconnectBackoffMultiplier = multiplier;
    }

    if (int.TryParse(section["TcpKeepAliveTime"], System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out var kaTime)) {
      options.TcpKeepAliveTime = kaTime;
    }

    if (int.TryParse(section["TcpKeepAliveInterval"], System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out var kaInterval)) {
      options.TcpKeepAliveInterval = kaInterval;
    }
  }

}

