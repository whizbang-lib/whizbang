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
/// Registers the elected partition assigner (#1254) with the Postgres notifications: the assignment store, the
/// claimers' cached assignment and its NOTIFY subscription, the assigner worker, the assigner role, and this
/// instance's connection mode.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignerRegistrationTests.cs:AddWhizbangPostgresNotifications_WiresThePartitionAssignerAsync</tests>
internal static class PartitionAssignerRegistration {
  internal static IServiceCollection AddWhizbangPartitionAssigner(this IServiceCollection services) {
    services.AddOptions<PartitionAssignerOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      IPostConfigureOptions<RoleAssignmentOptions>, PartitionAssignerRoleOptions>());

    // The registration records how this instance reaches the database (direct when its shared notify connection
    // resolved a connection of its own), without changing the heartbeat's cadence.
    services.TryAddSingletonOverNullDefault<IInstanceConnectionModeSource>(sp => sp.GetRequiredService<PgSharedNotifyConnection>());

    services.TryAddSingleton<IPartitionAssignmentStore, PgPartitionAssignmentStore>();
    services.TryAddSingleton<PartitionAssignmentCache>();
    services.TryAddSingleton<IPartitionAssignmentSource>(sp => sp.GetRequiredService<PartitionAssignmentCache>());
    services.AddHostedService<PartitionAssignmentSubscriber>();

    services.TryAddSingleton<PartitionAssignerWorker>();
    services.AddHostedService(sp => sp.GetRequiredService<PartitionAssignerWorker>());
    return services;
  }
}

/// <summary>The assigner's role is one of the roles held by assignment while the assigner is enabled.</summary>
internal sealed class PartitionAssignerRoleOptions(IOptions<PartitionAssignerOptions> assignerOptions)
  : IPostConfigureOptions<RoleAssignmentOptions> {
  public void PostConfigure(string? name, RoleAssignmentOptions options) {
    if (assignerOptions.Value.Enabled) {
      _ = options.Roles.Add(PartitionAssignerOptions.ROLE);
    }
  }
}

/// <summary>Subscribes the claimers' cached assignment to the assigner's announcements for the host's lifetime.</summary>
internal sealed class PartitionAssignmentSubscriber(ISharedNotifyConnection connection, PartitionAssignmentCache cache) : IHostedService {
  private IDisposable? _subscription;

  public Task StartAsync(CancellationToken cancellationToken) {
    _subscription = connection.Subscribe(cache);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    _subscription?.Dispose();
    _subscription = null;
    return Task.CompletedTask;
  }
}
