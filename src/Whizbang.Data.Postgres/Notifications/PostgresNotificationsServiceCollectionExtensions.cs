using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Notifications.AppSignals;

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
///       "ConnectionStringKey": "appservice-db",
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

    // System Signal Bus — the Postgres NOTIFY push transport. Registered alongside the
    // in-memory transport that AddWhizbangSignalBus registers by default. Both transports
    // fan out from the same SignalBus so callers get NOTIFY-backed cross-process delivery
    // plus in-process loopback for tests.
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
        // Nothing usable found — return a wrapper with DataSource=null so the
        // workers fall back to their string-based path AND surface the
        // operator-actionable startup diagnostic.
        return new NotificationDataSource(dataSource: null, ownsDataSource: false);
      }
      var builder = new NpgsqlDataSourceBuilder(connectionString);
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
  /// a consumer-style production deployments configure their EF Core DbContext via
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
