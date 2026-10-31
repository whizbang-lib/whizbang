// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The Postgres driver's workers each start at the stage they need (#1636): the shared notification connection and
/// its alive-lock at the end of Preflight, the duty holder (maintainer votes, and the vacuum on winning) and the
/// partition assigner at Coordinate. The work-available probes read nothing while the schema-ready gate is closed, and
/// the roles health component reads nothing before Elect.
/// </summary>
/// <remarks>
/// Each of them ran from the moment the host started, against tables the migration was altering, and before the
/// objects they read existed: the deadlocks and the 42P01 / 42883 stream on a fresh database.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/RoleAssignmentServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PartitionAssignerRegistration.cs</code-under-test>
[Category("Startup")]
[Category("Shard5")]
public sealed class StartupStageWiringTests {
  private static ServiceProvider _build() {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();
    services.AddSingleton<IWorkNotificationListener, NoOpWorkNotificationListener>();
    services.AddSingleton<ISchemaReadyGate>(new SchemaReadyGate());
    services.AddSingleton<IStartupStageGate>(new StartupStageGate(new SchemaReadyGate()));
    services.AddWhizbangPostgresNotifications();
    services.AddWhizbangPostgresNotifications();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task TheNotificationConnection_StartsAtTheEndOfPreflight_OnceAsync() {
    await using var provider = _build();
    var hosted = provider.GetServices<IHostedService>().ToList();

    var notify = hosted.OfType<StartupStageGatedService<PgSharedNotifyConnection>>().Single();
    await Assert.That(notify.Stage).IsEqualTo(StartupStage.Elect)
      .Because("the alive-lock and LISTEN come up as Preflight ends, before the election that reads them");
    await Assert.That(notify.Inner).IsSameReferenceAs(provider.GetRequiredService<PgSharedNotifyConnection>());
    await Assert.That(hosted.OfType<PgSharedNotifyConnection>()).IsEmpty()
      .Because("an ungated registration would start the connection with the host");
  }

  [Test]
  public async Task TheDutyHolderAndThePartitionAssigner_StartAtCoordinateAsync() {
    await using var provider = _build();
    var hosted = provider.GetServices<IHostedService>().ToList();

    var holder = hosted.OfType<StartupStageGatedService<DutyHolderWorker>>().Single();
    await Assert.That(holder.Stage).IsEqualTo(StartupStage.Coordinate);
    await Assert.That(holder.Inner).IsSameReferenceAs(provider.GetRequiredService<DutyHolderWorker>());
    var assigner = hosted.OfType<StartupStageGatedService<PartitionAssignerWorker>>().Single();
    await Assert.That(assigner.Stage).IsEqualTo(StartupStage.Coordinate);
    await Assert.That(assigner.Inner).IsSameReferenceAs(provider.GetRequiredService<PartitionAssignerWorker>());
    await Assert.That(hosted.OfType<DutyHolderWorker>()).IsEmpty();
    await Assert.That(hosted.OfType<PartitionAssignerWorker>()).IsEmpty();
  }

  [Test]
  public async Task EveryWorkAvailableProbe_ReadsTheSchemaReadyGateAsync() {
    await using var provider = _build();
    var gate = provider.GetRequiredService<ISchemaReadyGate>();

    var probes = provider.GetServices<ISignalSource>().ToList();
    await Assert.That(probes.OfType<PgOutboxWorkAvailablePollSource>().Single().SchemaReadyGate).IsSameReferenceAs(gate);
    await Assert.That(probes.OfType<PgInboxWorkAvailablePollSource>().Single().SchemaReadyGate).IsSameReferenceAs(gate);
    await Assert.That(probes.OfType<PgPerspectiveWorkAvailablePollSource>().Single().SchemaReadyGate).IsSameReferenceAs(gate);
    await Assert.That(probes.OfType<PgScheduleDuePollSource>().Single().SchemaReadyGate).IsSameReferenceAs(gate);
  }

  [Test]
  public async Task TheRolesHealthComponent_ReadsNothingBeforeElectAsync() {
    await using var provider = _build();
    var roles = provider.GetServices<IWhizbangHealthSource>().Single(s => s.Component == "roles");

    var health = await roles.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(health.Detail).IsEqualTo("role assignments are read from the Elect stage on; this instance is in an earlier one")
      .Because("the source was handed the stage gate, which has not reached Elect");
  }
}
