// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  /// Holds every duty as a liveness-tied assignment with a lease and an epoch (#2044), holds the
  /// duties in <see cref="RoleAssignmentOptions.Roles"/> (default: the maintainer, the migrator, and
  /// the commit-order stamper's role) from the holder loop, and releases held roles when the host
  /// stops. The Postgres driver registers this by default, so call it only to tune the options in
  /// code, or to replace an elector the host registered. Safe before or after the driver's
  /// registration, and more than once.
  /// </summary>
  /// <remarks>
  /// The options also bind from <c>Whizbang:Database:RoleAssignment</c>. During a rolling deploy from a
  /// release that held some duties by session lock, the vote sees such a holder through the holding it
  /// records, and a holder steps aside within one renewal when one appears, so no bridge is needed.
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

    // Replaces whatever elector is registered, the null default or a host's own. A later
    // AddWhizbangPostgresNotifications only displaces null defaults, so it leaves this in place.
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

    services.AddSingleton(sp => new PgRoleElector(
      sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>(),
      sp.GetRequiredService<IOptions<RoleAssignmentOptions>>(),
      sp.GetRequiredService<IConfiguration>(),
      sp.GetRequiredService<IServiceInstanceProvider>(),
      sp.GetRequiredService<ILogger<PgRoleElector>>(),
      sp.GetService<ILibraryVersionProvider>(),
      sp.GetService<INotificationConnectionStringFallback>(),
      sp.GetService<INotificationDataSource>(),
      metrics: sp.GetRequiredService<RoleAssignmentMetrics>(),
      // #2045: the instance's one run permit, when the host runs the worker pipeline (which registers it).
      runPermit: sp.GetService<Whizbang.Core.RunControl.WhizbangRunPermit>()));
    services.AddSingleton<IRoleAssignmentReader>(sp => sp.GetRequiredService<PgRoleElector>());
    // The health source reads through a deferred reader: listing health sources must not build the
    // elector, whose construction resolves the notification data source (and through it, under
    // auto-discovery, the application's DbContext model).
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IWhizbangHealthSource, RoleAssignmentHealthSource>(sp =>
      new RoleAssignmentHealthSource(
        new DeferredRoleAssignmentReader(sp),
        sp.GetRequiredService<IOptions<RoleAssignmentOptions>>(),
        sp.GetService<IStartupStageGate>())));

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
      [.. sp.GetServices<IDutyAcquisitionHandler>()],
      sp.GetRequiredService<IOptions<RoleAssignmentOptions>>(),
      sp.GetRequiredService<ILogger<DutyHolderWorker>>(),
      sp.GetService<Whizbang.Core.Signals.ISignalBus>(),
      sp.GetRequiredService<RoleAssignmentMetrics>()));
    // #1636: the holder loop votes, renews and runs owed work from Coordinate on. Before then its maintainer vote, and
    // the vacuum on winning, ran against tables the migration was altering and, on a fresh database, before they
    // existed. The migrator is never held here: it is elected in Elect, by the initializer.
    services.AddHostedServiceAtStage(StartupStage.Coordinate, sp => sp.GetRequiredService<DutyHolderWorker>());
    // #2045: the run-control path's role step releases every role on a stand-down, a pause or a stop.
    services.AddSingleton<Whizbang.Core.RunControl.IReleasesHeldRoles>(sp => sp.GetRequiredService<PgRoleElector>());
  }
}

/// <summary>
/// AOT-safe binding of <see cref="RoleAssignmentOptions"/> from <c>Whizbang:Database:RoleAssignment</c>:
/// <c>RenewInterval</c>, <c>MissedRenewalsBeforeLapse</c>, <c>CooldownAfterLapse</c>, <c>OwedWorkRetryBase</c> and
/// <c>ProgressWindow</c> (#2140), so an operator can tune the lease without a code change.
/// </summary>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentRegistrationTests.cs</tests>
internal sealed class ConfigureRoleAssignmentOptionsFromConfiguration(IConfiguration configuration)
  : IConfigureOptions<RoleAssignmentOptions> {
  public void Configure(RoleAssignmentOptions options) {
    var section = configuration.GetSection(RoleAssignmentServiceCollectionExtensions.CONFIGURATION_SECTION);
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
    if (TimeSpan.TryParse(section["ProgressWindow"], CultureInfo.InvariantCulture, out var progress)) {
      options.ProgressWindow = progress;
    }
  }
}

/// <summary>
/// Holds the commit-order stamper's leadership from the holder loop's roles (#966 phase 3): adds its
/// role, one per schema since the assignment table is per schema.
/// </summary>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitOrderStamperRoleTests.cs</tests>
internal sealed class CommitOrderStamperRoleOptions(IOptions<CommitOrderStamperOptions> stamperOptions)
  : IPostConfigureOptions<RoleAssignmentOptions> {
  public void PostConfigure(string? name, RoleAssignmentOptions options) {
    if (stamperOptions.Value.DisableStamper) {
      return;
    }
    _ = options.Roles.Add(CommitOrderStamperOptions.ROLE);
  }
}

/// <summary>
/// An <see cref="IRoleAssignmentReader"/> that resolves the registered reader on first use, so the
/// <c>roles</c> health source can be constructed without constructing the role elector.
/// </summary>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_RegistersAnEventStoreHealthSourceWiredToTheDriversProbeAsync</tests>
internal sealed class DeferredRoleAssignmentReader(IServiceProvider services) : IRoleAssignmentReader {
  public Task<IReadOnlyList<RoleAssignmentSnapshot>> ReadAssignmentsAsync(CancellationToken cancellationToken) =>
    services.GetRequiredService<IRoleAssignmentReader>().ReadAssignmentsAsync(cancellationToken);
}
