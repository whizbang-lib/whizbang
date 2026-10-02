using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>Registration for role assignment, which the Postgres driver applies by default.</summary>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentRegistrationTests.cs</tests>
public static class RoleAssignmentServiceCollectionExtensions {
  /// <summary>The configuration section role assignment binds from.</summary>
  public const string CONFIGURATION_SECTION = PostgresNotificationsServiceCollectionExtensions.CONFIGURATION_SECTION + ":RoleAssignment";

  /// <summary>
  /// Holds the duties in <see cref="RoleAssignmentOptions.Roles"/> (default: the maintainer, the
  /// migrator, and the commit-order stamper's role) as liveness-tied assignments with an epoch,
  /// instead of session advisory locks, and releases held roles when the host stops. The Postgres
  /// driver registers this by default, so call it only to tune the options in code, or to replace an
  /// elector the host registered. Safe before or after the driver's registration, and more than once.
  /// </summary>
  /// <remarks>
  /// The options also bind from <c>Whizbang:Database:RoleAssignment</c>. The legacy-lock bridge
  /// (<see cref="RoleAssignmentOptions.HoldLegacySessionLock"/>) is on by default in this release, so a
  /// rolling deploy from a release that held duties by session lock never has both acting.
  /// </remarks>
  /// <param name="services">The service collection.</param>
  /// <param name="configure">Optional tuning.</param>
  /// <returns>The same collection.</returns>
  public static IServiceCollection AddWhizbangRoleAssignment(
      this IServiceCollection services, Action<RoleAssignmentOptions>? configure = null) {
    ArgumentNullException.ThrowIfNull(services);

    var options = services.AddOptions<RoleAssignmentOptions>();
    if (configure is not null) {
      options.Configure(configure);
    }

    if (services.Any(d => d.ServiceType == typeof(PgRoleElector))) {
      return services;
    }
    _addCore(services);

    // Replaces whatever elector is registered, the null default or the session-lock elector. A
    // later AddWhizbangPostgresNotifications only displaces null defaults, so it leaves this in place.
    services.RemoveAll<IDutyElector>();
    services.AddSingleton<IDutyElector>(sp => sp.GetRequiredService<PgRoleElector>());
    return services;
  }

  /// <summary>
  /// The driver's default: role assignment, unless the host registered its own elector, which is
  /// left in place (and then nothing here is registered at all).
  /// </summary>
  internal static IServiceCollection AddWhizbangRoleAssignmentByDefault(this IServiceCollection services) {
    services.AddOptions<RoleAssignmentOptions>();
    if (services.Any(d => d.ServiceType == typeof(PgRoleElector))
        || services.Any(d => d.ServiceType == typeof(IDutyElector) && !_isNullDefault(d))) {
      return services;
    }
    _addCore(services);
    services.TryAddSingletonOverNullDefault<IDutyElector>(sp => sp.GetRequiredService<PgRoleElector>());
    return services;
  }

  private static bool _isNullDefault(ServiceDescriptor descriptor) =>
    descriptor.ImplementationInstance is INullDefault
    || (descriptor.ImplementationType is { } implementation && typeof(INullDefault).IsAssignableFrom(implementation));

  private static void _addCore(IServiceCollection services) {
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      IConfigureOptions<RoleAssignmentOptions>, ConfigureRoleAssignmentOptionsFromConfiguration>());

    services.AddMetrics();
    services.TryAddSingleton<WhizbangMetrics>();
    services.TryAddSingleton<RoleAssignmentMetrics>();

    // The session-lock elector, as a concrete service: the role elector's delegate.
    services.TryAddSingleton<PgDutyElector>();
    services.AddSingleton(sp => new PgRoleElector(
      sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>(),
      sp.GetRequiredService<IOptions<RoleAssignmentOptions>>(),
      sp.GetRequiredService<IConfiguration>(),
      sp.GetRequiredService<IServiceInstanceProvider>(),
      sp.GetRequiredService<PgDutyElector>(),
      sp.GetRequiredService<ILogger<PgRoleElector>>(),
      sp.GetService<ILibraryVersionProvider>(),
      sp.GetService<INotificationConnectionStringFallback>(),
      sp.GetService<INotificationDataSource>(),
      metrics: sp.GetRequiredService<RoleAssignmentMetrics>()));
    services.AddSingleton<IRoleAssignmentReader>(sp => sp.GetRequiredService<PgRoleElector>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IWhizbangHealthSource, RoleAssignmentHealthSource>());

    // Pending duty work, and the holder loop that runs it: a skipped duty step is owed to the role
    // (the startup pipeline resolves this store), and whichever instance holds the role runs it.
    services.TryAddSingletonOverNullDefault<IPendingDutyWorkStore>(sp => new PgPendingDutyWorkStore(
      sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>(),
      sp.GetRequiredService<IOptions<RoleAssignmentOptions>>(),
      sp.GetRequiredService<IConfiguration>(),
      sp.GetRequiredService<IServiceInstanceProvider>(),
      sp.GetService<INotificationConnectionStringFallback>(),
      sp.GetService<INotificationDataSource>()));
    services.AddSingleton(sp => new DutyHolderWorker(
      sp.GetRequiredService<IDutyElector>(),
      sp.GetRequiredService<IPendingDutyWorkStore>(),
      [.. sp.GetServices<IDutyWorkHandler>(),
       .. sp.GetServices<IStartupStep>()
         .Where(step => step.Descriptor.RequiredCapability != StartupCapabilities.EVERY_INSTANCE)
         .Select(step => new StartupStepDutyWork(step, sp.GetService<IStartupPipelineState>()))],
      sp.GetRequiredService<IOptions<RoleAssignmentOptions>>(),
      sp.GetRequiredService<ILogger<DutyHolderWorker>>(),
      sp.GetService<ISharedNotifyConnection>(),
      sp.GetRequiredService<RoleAssignmentMetrics>()));
    services.AddHostedService(sp => sp.GetRequiredService<DutyHolderWorker>());
    services.AddSingleton<IReleasesDutiesOnShutdown>(sp => sp.GetRequiredService<PgRoleElector>());
    services.AddHostedService<DutyShutdownReleaseService>();
  }
}

/// <summary>
/// AOT-safe binding of <see cref="RoleAssignmentOptions"/> from <c>Whizbang:Database:RoleAssignment</c>:
/// <c>Enabled</c>, <c>HoldLegacySessionLock</c>, <c>RenewInterval</c>, <c>MissedRenewalsBeforeLapse</c>,
/// <c>CooldownAfterLapse</c> and <c>OwedWorkRetryBase</c>, so an operator can turn the bridge on for one
/// rolling deploy, or role assignment off, without a code change.
/// </summary>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentRegistrationTests.cs</tests>
internal sealed class ConfigureRoleAssignmentOptionsFromConfiguration(IConfiguration configuration)
  : IConfigureOptions<RoleAssignmentOptions> {
  public void Configure(RoleAssignmentOptions options) {
    var section = configuration.GetSection(RoleAssignmentServiceCollectionExtensions.CONFIGURATION_SECTION);
    if (bool.TryParse(section["Enabled"], out var enabled)) {
      options.Enabled = enabled;
    }
    if (bool.TryParse(section["HoldLegacySessionLock"], out var bridge)) {
      options.HoldLegacySessionLock = bridge;
    }
    if (TimeSpan.TryParse(section["RenewInterval"], CultureInfo.InvariantCulture, out var renew)) {
      options.RenewInterval = renew;
    }
    if (int.TryParse(section["MissedRenewalsBeforeLapse"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var missed)) {
      options.MissedRenewalsBeforeLapse = missed;
    }
    if (TimeSpan.TryParse(section["CooldownAfterLapse"], CultureInfo.InvariantCulture, out var cooldown)) {
      options.CooldownAfterLapse = cooldown;
    }
    if (TimeSpan.TryParse(section["OwedWorkRetryBase"], CultureInfo.InvariantCulture, out var retry)) {
      options.OwedWorkRetryBase = retry;
    }
  }
}

/// <summary>
/// Holds the commit-order stamper's leadership by assignment (#966 phase 3): adds its role, one per
/// schema since the assignment table is per schema, and names its legacy leader lock so a bridged
/// holder, or a vote seeing an old stamper, uses the lock an old stamper takes.
/// </summary>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitOrderStamperRoleTests.cs</tests>
internal sealed class CommitOrderStamperRoleOptions(IOptions<CommitOrderStamperOptions> stamperOptions)
  : IPostConfigureOptions<RoleAssignmentOptions> {
  public void PostConfigure(string? name, RoleAssignmentOptions options) {
    var stamper = stamperOptions.Value;
    if (stamper.DisableStamper) {
      return;
    }
    _ = options.Roles.Add(CommitOrderStamperOptions.ROLE);
    var baseKey = stamper.AdvisoryLockKey;
    options.LegacyLockKeys[CommitOrderStamperOptions.ROLE] = schema => CommitOrderStamperLockKey.Compute(schema, baseKey);
  }
}
