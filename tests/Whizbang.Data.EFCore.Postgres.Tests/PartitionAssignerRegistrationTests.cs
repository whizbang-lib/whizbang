// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The Postgres notifications wire the elected partition assigner (#1254): the store, the claimers' cached assignment
/// and its subscription, the worker, the role, and the instance's connection mode. Composition only: nothing starts.
/// </summary>
[Category("Shard3")]
public class PartitionAssignerRegistrationTests {
  private static ServiceProvider _build(Action<IServiceCollection>? extra = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();
    services.AddSingleton<IWorkNotificationListener, NoOpWorkNotificationListener>();
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    services.AddWhizbangPostgresNotifications();
    extra?.Invoke(services);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_WiresThePartitionAssignerAsync() {
    await using var sp = _build();

    await Assert.That(sp.GetRequiredService<IPartitionAssignmentSource>()).IsSameReferenceAs(sp.GetRequiredService<PartitionAssignmentCache>());
    await Assert.That(sp.GetRequiredService<IPartitionAssignmentStore>()).IsTypeOf<PgPartitionAssignmentStore>();
    await Assert.That(sp.GetRequiredService<IInstanceConnectionModeSource>()).IsSameReferenceAs(sp.GetRequiredService<PgSharedNotifyConnection>());
    var hosted = sp.GetServices<IHostedService>().ToList();
    await Assert.That(hosted.OfType<PartitionAssignerWorker>().Count()).IsEqualTo(1);
    await Assert.That(hosted.OfType<PartitionAssignmentSubscriber>().Count()).IsEqualTo(1);
    await Assert.That(sp.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value.Roles).Contains(PartitionAssignerOptions.ROLE);
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_WithTheAssignerDisabled_LeavesItsRoleOutAsync() {
    await using var sp = _build(services => services.Configure<PartitionAssignerOptions>(o => o.Enabled = false));

    await Assert.That(sp.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value.Roles).DoesNotContain(PartitionAssignerOptions.ROLE);
  }
}
