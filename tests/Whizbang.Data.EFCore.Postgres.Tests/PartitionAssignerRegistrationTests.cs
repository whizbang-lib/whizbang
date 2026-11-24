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
  private static ServiceProvider _build(Action<IServiceCollection>? extra = null, Dictionary<string, string?>? settings = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build());
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
    await Assert.That(hosted.OfType<StartupStageGatedService<PartitionAssignerWorker>>().Single().Inner)
      .IsSameReferenceAs(sp.GetRequiredService<PartitionAssignerWorker>())
      .Because("the assigner starts at Coordinate (#1636), through the wrapper that waits for the stage");
    await Assert.That(hosted.OfType<PartitionAssignmentSubscriber>().Count()).IsEqualTo(1);
    await Assert.That(sp.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value.Roles).Contains(PartitionAssignerOptions.ROLE);
  }

  [Test]
  public async Task TheRemovedEnabledKey_SetToFalse_TurnsNothingOffAsync() {
    // #2559: the assigner always runs. The removed key is reported at startup as removed, and the role is still held.
    await using var sp = _build(settings: new Dictionary<string, string?> { ["Whizbang:Database:PartitionAssigner:Enabled"] = "false" });

    await Assert.That(sp.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value.Roles).Contains(PartitionAssignerOptions.ROLE)
      .Because("the published assignment is the one source of stream ownership, so nothing turns its assigner off");
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_BindsEveryAssignerKeyFromConfigurationAsync() {
    await using var sp = _build(settings: new() {
      ["Whizbang:Database:PartitionAssigner:SettleWindow"] = "00:00:09",
      ["Whizbang:Database:PartitionAssigner:AssignmentLease"] = "00:02:00",
      ["Whizbang:Database:PartitionAssigner:RepublishInterval"] = "00:07:00",
      ["Whizbang:Database:PartitionAssigner:HandoverDeadline"] = "00:00:45",
    });

    var options = sp.GetRequiredService<IOptions<PartitionAssignerOptions>>().Value;

    await Assert.That(options.SettleWindow).IsEqualTo(TimeSpan.FromSeconds(9))
      .Because("#2558: the settle window is measured on a deployed environment, so an operator must be able to set it");
    await Assert.That(options.AssignmentLease).IsEqualTo(TimeSpan.FromMinutes(2));
    await Assert.That(options.RepublishInterval).IsEqualTo(TimeSpan.FromMinutes(7));
    await Assert.That(options.HandoverDeadline).IsEqualTo(TimeSpan.FromSeconds(45))
      .Because("#2557: the handover deadline is measured on a deployed environment, so an operator must be able to set it");
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_WithNoAssignerKeys_KeepsTheDefaultsAsync() {
    await using var sp = _build(settings: new() {
      ["Whizbang:Database:PartitionAssigner:SettleWindow"] = "not a duration",
    });

    var options = sp.GetRequiredService<IOptions<PartitionAssignerOptions>>().Value;

    await Assert.That(options.SettleWindow).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(options.AssignmentLease).IsEqualTo(TimeSpan.FromSeconds(90));
    await Assert.That(options.RepublishInterval).IsEqualTo(TimeSpan.FromMinutes(5));
    await Assert.That(options.HandoverDeadline).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_AHostsOwnConfigureOfTheAssigner_StillWinsAsync() {
    await using var sp = _build(
      services => services.Configure<PartitionAssignerOptions>(o => o.SettleWindow = TimeSpan.FromSeconds(1)),
      new() { ["Whizbang:Database:PartitionAssigner:SettleWindow"] = "00:00:09" });

    await Assert.That(sp.GetRequiredService<IOptions<PartitionAssignerOptions>>().Value.SettleWindow).IsEqualTo(TimeSpan.FromSeconds(1));
  }
}
