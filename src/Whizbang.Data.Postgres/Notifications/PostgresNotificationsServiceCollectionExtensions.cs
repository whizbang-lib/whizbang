using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
///       "ConnectionStringKey": "bffservice-db",
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
    services.AddHostedService<PgCommitOrderStamperWorker>();

    // App-signal channel (publishes pg_notify on wh_app_<topic>).
    services.TryAddSingleton<IAppSignalChannel, PgAppSignalChannel>();

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
  }
}
