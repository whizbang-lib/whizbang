// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Whizbang.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Registers the elected partition assigner (#1254) for every Postgres driver (#2559), through
/// <see cref="PostgresCoordinationRegistration"/>: the assignment store, the claimers' cached assignment and its
/// subscription to publish announcements, the assigner worker, and the assigner role.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignerRegistrationTests.cs:AddWhizbangPostgresNotifications_WiresThePartitionAssignerAsync</tests>
internal static class PartitionAssignerRegistration {
  /// <summary>The configuration section the assigner's options bind from (#2558).</summary>
  internal const string CONFIGURATION_SECTION = PostgresNotificationsServiceCollectionExtensions.CONFIGURATION_SECTION + ":PartitionAssigner";

  internal static IServiceCollection AddWhizbangPartitionAssigner(this IServiceCollection services) {
    services.AddOptions<PartitionAssignerOptions>();
    // #2558: bound from configuration, so an operator can set the settle window measured on a deployed environment. A
    // host's own Configure registered after this still has the last word.
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      IConfigureOptions<PartitionAssignerOptions>, ConfigurePartitionAssignerOptionsFromConfiguration>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      IPostConfigureOptions<RoleAssignmentOptions>, PartitionAssignerRoleOptions>());

    services.TryAddSingleton<IPartitionAssignmentStore, PgPartitionAssignmentStore>();
    services.TryAddSingleton<PartitionAssignmentCache>();
    // Over a null source registered before it (a host's or a test's), in either order.
    services.TryAddSingletonOverNullDefault<IPartitionAssignmentSource>(sp => sp.GetRequiredService<PartitionAssignmentCache>());
    services.AddHostedService<PartitionAssignmentSubscriber>();

    services.TryAddSingleton<PartitionAssignerWorker>();
    // #1636: the assigner is elected and publishes from Coordinate on. Before then it voted against tables the
    // migration was altering and, on a fresh database, before they existed.
    services.AddHostedServiceAtStage(StartupStage.Coordinate, sp => sp.GetRequiredService<PartitionAssignerWorker>());
    return services;
  }
}

/// <summary>
/// Binds <see cref="PartitionAssignerOptions"/> from <c>Whizbang:Database:PartitionAssigner</c> (#2558):
/// <c>AssignmentLease</c>, <c>RepublishInterval</c>, <c>SettleWindow</c>, <c>HandoverDeadline</c> and <c>OwnershipByMap</c> (#2557). <c>Enabled</c> is retired (#2559) and never
/// bound. A key that is absent or does not parse leaves the default. Parsed by hand, as the role assignment's options are, so the binding needs no reflection.
/// </summary>
/// <docs>operations/configuration/configuration-reference#partition-assigner</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignerRegistrationTests.cs:AddWhizbangPostgresNotifications_BindsEveryAssignerKeyFromConfigurationAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignerRegistrationTests.cs:AddWhizbangPostgresNotifications_WithNoAssignerKeys_KeepsTheDefaultsAsync</tests>
internal sealed class ConfigurePartitionAssignerOptionsFromConfiguration(IConfiguration configuration)
  : IConfigureOptions<PartitionAssignerOptions> {
  public void Configure(PartitionAssignerOptions options) {
    var section = configuration.GetSection(PartitionAssignerRegistration.CONFIGURATION_SECTION);
    if (TimeSpan.TryParse(section[nameof(PartitionAssignerOptions.AssignmentLease)], CultureInfo.InvariantCulture, out var lease)) {
      options.AssignmentLease = lease;
    }
    if (TimeSpan.TryParse(section[nameof(PartitionAssignerOptions.RepublishInterval)], CultureInfo.InvariantCulture, out var backstop)) {
      options.RepublishInterval = backstop;
    }
    if (TimeSpan.TryParse(section[nameof(PartitionAssignerOptions.SettleWindow)], CultureInfo.InvariantCulture, out var settle)) {
      options.SettleWindow = settle;
    }
    if (TimeSpan.TryParse(section[nameof(PartitionAssignerOptions.HandoverDeadline)], CultureInfo.InvariantCulture, out var deadline)) {
      options.HandoverDeadline = deadline;
    }
    if (bool.TryParse(section[nameof(PartitionAssignerOptions.OwnershipByMap)], out var byMap)) {
      options.OwnershipByMap = byMap;
    }
  }
}

/// <summary>The assigner's role is always one of the roles held by assignment (#2559): nothing turns the assigner off.</summary>
internal sealed class PartitionAssignerRoleOptions : IPostConfigureOptions<RoleAssignmentOptions> {
  public void PostConfigure(string? name, RoleAssignmentOptions options) => _ = options.Roles.Add(PartitionAssignerOptions.ROLE);
}

/// <summary>
/// Subscribes the claimers' cached assignment to the assigner's announcements on the signal bus for the host's lifetime
/// (#2046).
/// </summary>
internal sealed class PartitionAssignmentSubscriber(Whizbang.Core.Signals.ISignalBus bus, PartitionAssignmentCache cache) : IHostedService {
  private IDisposable? _subscription;

  public Task StartAsync(CancellationToken cancellationToken) {
    _subscription = bus.Subscribe<Whizbang.Core.Signals.PartitionAssignmentPublishedSignal>(published => {
      cache.OnPublished(published.Version);
      return ValueTask.CompletedTask;
    });
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    _subscription?.Dispose();
    _subscription = null;
    return Task.CompletedTask;
  }
}
