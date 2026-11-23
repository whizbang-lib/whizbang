// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  internal static IServiceCollection AddWhizbangPartitionAssigner(this IServiceCollection services) {
    services.AddOptions<PartitionAssignerOptions>();
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
