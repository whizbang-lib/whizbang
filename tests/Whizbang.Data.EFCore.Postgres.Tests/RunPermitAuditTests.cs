// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every hosted service a turnkey host registers says how it stands toward the instance's one run permit (#2045). The
/// host is composed the way a consumer composes one and built, not started; each hosted service must be one of:
/// <list type="bullet">
///   <item>a worker that takes work for this instance and obeys the permit itself: it is an <see cref="IObeysRunPermit"/>
///     holding the container's one permit, so the run-control path that closes that permit stops it;</item>
///   <item>a worker that does fleet-wide work only as the maintainer, which obeys the permit through the one maintainer
///     question (<see cref="MaintainerDuty.IsThisInstancesTurn"/>);</item>
///   <item>a worker that holds a role, which obeys it through the elector: a closed permit refuses every vote;</item>
///   <item>a worker that finishes work this instance already holds, which must keep running while the permit is closed
///     so the work in hand is finished rather than abandoned;</item>
///   <item>a service that takes no work.</item>
/// </list>
/// A hosted service added without a place here fails, naming it, so its author decides how it obeys a stand-down, a
/// pause and a stop rather than taking work through all three.
/// </summary>
/// <code-under-test>src/Whizbang.Core/RunControl/WhizbangRunPermit.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerBuilderExtensions.cs</code-under-test>
/// <docs>resilience/managed-resource-run-control</docs>
[Category("RunControl")]
[Category("Shard5")]
public sealed class RunPermitAuditTests {
  /// <summary>Takes work for this instance and obeys the permit itself.</summary>
  private static readonly HashSet<string> _obeysThePermit = new(StringComparer.Ordinal) {
    "ClaimWorker", "CoalesceShipWorker", "MultiStreamCommandWorker", "ScheduleWorker", "TransportConsumerWorker",
    // Registered by the host itself rather than the library; it obeys the container's permit by default all the same.
    "ServiceBusConsumerWorker",
  };

  /// <summary>Does fleet-wide work only on its maintainer turn, which a closed permit denies.</summary>
  private static readonly HashSet<string> _obeysThroughTheMaintainerDuty = new(StringComparer.Ordinal) {
    "IntegrityAuditWorker", "IntegrityCheckpointWorker", "IntegrityDeliveryWorker", "RepairDrainWorker", "MaintenanceWorker",
    "OriginReattributionWorker", "SubscriptionBackfillWorker", "IntegrityLedgerRecheckWorker",
  };

  /// <summary>Holds a role, and a closed permit refuses every vote.</summary>
  private static readonly HashSet<string> _obeysThroughTheElector = new(StringComparer.Ordinal) {
    "DutyHolderWorker", "PartitionAssignerWorker", "PgCommitOrderStamperWorker",
  };

  /// <summary>Finishes, flushes or keeps alive the work this instance already holds; it takes none.</summary>
  private static readonly HashSet<string> _finishesHeldWork = new(StringComparer.Ordinal) {
    "OutboxDrainWorker", "InboxDrainWorker", "PerspectiveWorker", "InboxDispatchWorker", "OutboxPublishWorker",
    "InboxHandlerWorker", "OutboxCompletionFlushWorker", "PerspectiveCompletionFlushWorker", "FailureFlushWorker",
    "LeaseRenewalWorker", "BatchStrategyShutdownFlush",
  };

  /// <summary>Takes no work for this instance.</summary>
  private static readonly HashSet<string> _takesNoWork = new(StringComparer.Ordinal) {
    // The coordination plane: liveness, the notification connection, the standby handshake.
    "PgSharedNotifyConnection", "SignalBusHostedService", "HeartbeatWorker", "PgInstanceLifecycleMonitor", "StandbyWatcher",
    "PgDurableSignalRetentionWorker", "PgDurableSignalTailWorker", "WhizbangShutdownService",
    // Startup and its stages; host shutdown takes the run-control path from StartupStageHostLifecycle.
    "WhizbangDatabaseInitializerService", "StartupPipelineWorker", "StartupStageHostLifecycle", "StartupReadyService",
    "LifecyclePhaseWorker", "ReadModelsReadyDriver", "TypeDefinitionReconcilerHostedService",
    // Measurement and in-process state.
    "TableStatisticsCollector", "EventTypeBytesSampler", "NotifyDebounceStatsCollector", "BacklogAgeWorker",
    "RecentlyProcessedEventCacheSweepWorker", "InProcessTrackerSweepWorker", "HealthCheckPublisherHostedService",
    // Wakes, registrations and checks: each wakes or configures a worker above, which obeys the permit itself.
    "BackupTickCoordinator", "PgWorkNotificationListener", "MultiStreamCommandSubscriber", "PartitionAssignmentSubscriber",
    "UnknownConfigurationKeyStartupValidator", "RegistrationValidationStartup", "InertConcurrencyStartupReporter",
    // Reports items two assemblies both register, from what the registrations already hold (#2201).
    "RegistrationCollisionStartupValidator",
    // Logs at Debug which assemblies the host's service scope included and excluded, from the anchors (#2243).
    "ServiceAssemblyScopeStartupReporter",
    "WhizbangStartupLogger", "UnsealedOffloadStartupWarning", "TagPolicyStartupValidator", "NoTransportConfiguredNotice",
    "UnobservedExceptionDiagnosticsWarmUp", "PerspectiveRowRetentionConfigurator", "ArchivedStreamRowsConfigurator",
    "CapabilityVerdictEnforcer", "MultiStreamCommandReceptorRegistrar", "IntegrityCheckpointReceptorRegistrar",
    "IntegrityManifestReceptorRegistrar", "RebuildCommandReceptorRegistrar", "RedeliveryRequestReceptorRegistrar",
    "ScheduledPrefixTruncateReceptorRegistrar", "ScheduledIntegritySweepReceptorRegistrar", "SagaWatchdogTickRouterRegistrar",
    "DefaultBackupTickRegistrar", "IdleActivityTouchHookBinder", "IntegritySweepScheduler",
    // Operator-driven or fleet-wide recovery: it returns dead letters to the queues for every instance; this instance
    // takes them back only through the claim worker and the consumer, which obey the permit.
    "DeadLetterRecoveryWorker", "TransportDeadLetterDrainWorker", "SubscriptionExpansionWorker",
    // Fed only by a command receptor, whose dispatch arrives through the obeying consumer and claim worker.
    "RebuildBackgroundWorker",
  };

  /// <summary>#2092: the paced backfill is hosted by the turnkey pipeline, and does its work on the maintainer's turn.</summary>
  [Test]
  public async Task TheTurnkeyHost_RunsThePacedBackfillAsync() {
    using var host = _turnkeyHost();
    var hosted = host.Services.GetServices<IHostedService>()
      .Select(service => service is GatedHostedService gated ? gated.Service : service)
      .Select(service => service.GetType().Name)
      .ToList();

    await Assert.That(hosted).Contains("SubscriptionBackfillWorker");
  }

  [Test]
  public async Task EveryHostedService_SaysHowItObeysTheRunPermit_AndTheObeyersHoldTheOnePermitAsync() {
    using var host = _turnkeyHost();
    var permit = host.Services.GetRequiredService<WhizbangRunPermit>();
    var hosted = host.Services.GetServices<IHostedService>()
      .Select(service => service is GatedHostedService gated ? gated.Service : service)
      .ToList();

    var unplaced = hosted.Select(s => s.GetType().Name).Distinct()
      .Where(name => !_obeysThePermit.Contains(name) && !_obeysThroughTheMaintainerDuty.Contains(name)
        && !_obeysThroughTheElector.Contains(name) && !_finishesHeldWork.Contains(name) && !_takesNoWork.Contains(name))
      .ToList();
    await Assert.That(unplaced).IsEmpty()
      .Because("every hosted service must say how a stand-down, a pause and a stop reach it: " + string.Join(", ", unplaced));

    var disobedient = hosted
      .Where(s => _obeysThePermit.Contains(s.GetType().Name)
        && (s is not IObeysRunPermit obeyer || !ReferenceEquals(obeyer.RunPermit, permit)))
      .Select(s => s.GetType().Name)
      .ToList();
    await Assert.That(disobedient).IsEmpty()
      .Because("a worker that takes work obeys the container's one run permit, through its registration: "
        + string.Join(", ", disobedient));
    var obeyers = hosted.Where(s => _obeysThePermit.Contains(s.GetType().Name)).Select(s => s.GetType().Name).ToHashSet();
    await Assert.That(obeyers).IsEquivalentTo(_obeysThePermit)
      .Because("the scan must see every worker it vouches for, or it vouches for nothing");

    var controller = host.Services.GetRequiredService<IWhizbangRunController>();
    await Assert.That(controller.Permit).IsSameReferenceAs(permit);
    await Assert.That(host.Services.GetServices<IReleasesHeldWork>().Select(r => r.GetType().Name)).Contains("ClaimWorker")
      .Because("the claim worker's leased streams are released by the path once nothing is in hand");
    await Assert.That(host.Services.GetServices<IReleasesHeldRoles>().Single()).IsTypeOf<PgRoleElector>()
      .Because("the roles are released by the path's role step");
    await Assert.That(host.Services.GetRequiredService<PgRoleElector>().RunPermit).IsSameReferenceAs(permit)
      .Because("the elector refuses every vote while the permit is closed");
  }

  private static IHost _turnkeyHost() {
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:db"] = "Host=localhost;Database=audit;Username=audit;Password=audit",
    });
    builder.Services.AddSingleton<ITransport>(new InProcessTransport());
    _ = builder.Services.AddWhizbang().WithRouting(static r => r.OwnDomains("audit.commands"))
      .WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres.AddTransportConsumer();
    // The legacy Service Bus consumer is registered by a host, the way a host registers it, not by the library: the
    // permit must still reach it through the container.
    builder.Services.AddHostedService<ServiceBusConsumerWorker>();
    return builder.Build();
  }
}
