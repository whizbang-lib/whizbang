using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Health;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>Opt-in registration for role assignment.</summary>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentRegistrationTests.cs</tests>
public static class RoleAssignmentServiceCollectionExtensions {
  /// <summary>
  /// Holds the duties in <see cref="RoleAssignmentOptions.Roles"/> (default: the maintainer) as
  /// liveness-tied assignments with an epoch, instead of session advisory locks. Every other duty,
  /// the migrator included, keeps the session-lock elector, and held roles are released when the
  /// host stops. Safe to call before or after the Postgres driver's registration, and more than once.
  /// </summary>
  /// <remarks>
  /// The legacy-lock bridge is on by default, so a fleet that still has instances on the
  /// session-lock elector never has both acting. Turn it off only when none remain.
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
      sp.GetService<INotificationConnectionStringFallback>(),
      sp.GetService<INotificationDataSource>(),
      metrics: sp.GetRequiredService<RoleAssignmentMetrics>()));
    services.AddSingleton<IRoleAssignmentReader>(sp => sp.GetRequiredService<PgRoleElector>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IWhizbangHealthSource, RoleAssignmentHealthSource>());

    // Pending duty work, and the holder loop that runs it: a skipped duty step is owed to the role
    // (the startup pipeline resolves this store), and whichever instance holds the role runs it.
    services.TryAddSingleton<IPendingDutyWorkStore>(sp => new PgPendingDutyWorkStore(
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

    // Replaces whatever elector is registered, the null default or the session-lock elector. A
    // later AddWhizbangPostgresNotifications only displaces null defaults, so it leaves this in place.
    services.RemoveAll<IDutyElector>();
    services.AddSingleton<IDutyElector>(sp => sp.GetRequiredService<PgRoleElector>());
    services.AddSingleton<IReleasesDutiesOnShutdown>(sp => sp.GetRequiredService<PgRoleElector>());
    services.AddHostedService<DutyShutdownReleaseService>();
    return services;
  }
}
