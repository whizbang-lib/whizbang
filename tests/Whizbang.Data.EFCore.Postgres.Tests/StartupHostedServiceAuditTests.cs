// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Signals;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every hosted service a turnkey host registers says when it may first touch the database (#1636). The host is
/// composed the way a consumer composes one (the EF Core Postgres driver, its notification stack, the worker pipeline
/// and a transport) and built, not started; each hosted service it resolves must be one of:
/// <list type="bullet">
///   <item>started at a stage, through <see cref="StartupStageGatedService{TService}"/>, at the stage this table names;</item>
///   <item>a worker whose own loop waits for the schema-ready gate, which opens at the end of Coordinate;</item>
///   <item>schema initialization and the startup machinery itself, which is what Preflight is;</item>
///   <item>a service that does no database work when it starts (in-memory registration, configuration checks, a
///     subscription on the notification connection, or work done only on stop).</item>
/// </list>
/// </summary>
/// <remarks>
/// A hosted service added without a place in this table fails here, naming it, so the author has to decide its stage
/// rather than starting it with the host by default. The list is the composition's own answer, which is the check on
/// the source search that produced the table.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageGatedService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Signals/SignalBusServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs</code-under-test>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
[Category("Startup")]
[Category("Shard5")]
public sealed class StartupHostedServiceAuditTests {
  /// <summary>The services that start at a stage, and the stage.</summary>
  private static readonly Dictionary<string, StartupStage> _atStage = new(StringComparer.Ordinal) {
    [nameof(PgSharedNotifyConnection)] = StartupStage.Elect,
    [nameof(SignalBusHostedService)] = StartupStage.Elect,
    [nameof(DutyHolderWorker)] = StartupStage.Coordinate,
    [nameof(PartitionAssignerWorker)] = StartupStage.Coordinate,
  };

  /// <summary>Workers whose own loop waits for the schema-ready gate before their first statement.</summary>
  private static readonly HashSet<string> _afterTheSchemaReadyGate = new(StringComparer.Ordinal) {
    "BackupTickCoordinator", "ClaimWorker", "CoalesceShipWorker", "DeadLetterRecoveryWorker",
    "EventTypeBytesSampler", "FailureFlushWorker", "HeartbeatWorker", "InboxDispatchWorker", "InboxDrainWorker",
    "InboxHandlerWorker", "IntegrityAuditWorker", "IntegrityCheckpointWorker", "IntegrityDeliveryWorker",
    "IntegritySweepScheduler",
    "LeaseRenewalWorker", "LifecyclePhaseWorker", "MaintenanceWorker", "MessageTypeRegistryReconciliationHostedService",
    "MultiStreamCommandWorker", "NotifyDebounceStatsCollector", "OrphanInboxJanitor", "OutboxCompletionFlushWorker",
    "OutboxDrainWorker", "OutboxPublishWorker", "PerspectiveCompletionFlushWorker", "PerspectiveMigrationWorker",
    "PerspectiveWorker", "PgCommitOrderStamperWorker", "PgDurableSignalRetentionWorker", "PgDurableSignalTailWorker",
    "PgInstanceLifecycleMonitor", "ReadModelsReadyDriver", "RepairDrainWorker", "ScheduleWorker",
    "ScheduledIntegritySweepReceptorRegistrar", "StandbyWatcher", "SubscriptionExpansionWorker",
    "TableStatisticsCollector", "TransportConsumerWorker", "TransportDeadLetterDrainWorker",
    "TypeDefinitionReconcilerHostedService",
  };

  /// <summary>Preflight itself: schema initialization, and the machinery that records and reports the stages.</summary>
  private static readonly HashSet<string> _preflight = new(StringComparer.Ordinal) {
    "WhizbangDatabaseInitializerService", "StartupPipelineWorker", "StartupStageHostLifecycle", "StartupReadyService",
  };

  /// <summary>Services that do no database work when they start.</summary>
  private static readonly HashSet<string> _noDatabaseWorkAtStart = new(StringComparer.Ordinal) {
    // Configuration and registration checks, and log lines.
    "UnknownConfigurationKeyStartupValidator", "RegistrationValidationStartup", "InertConcurrencyStartupReporter",
    "WhizbangStartupLogger", "UnsealedOffloadStartupWarning", "TagPolicyStartupValidator", "NoTransportConfiguredNotice",
    "UnobservedExceptionDiagnosticsWarmUp", "PerspectiveRowRetentionConfigurator", "CapabilityVerdictEnforcer",
    // In-memory registrations: receptors, backup ticks (run by BackupTickCoordinator), event hooks.
    "MultiStreamCommandReceptorRegistrar", "IntegrityCheckpointReceptorRegistrar", "IntegrityManifestReceptorRegistrar",
    "RebuildCommandReceptorRegistrar", "RedeliveryRequestReceptorRegistrar", "ScheduledStreamCloseReceptorRegistrar",
    "SagaWatchdogTickRouterRegistrar", "DefaultBackupTickRegistrar", "IdleActivityTouchHookBinder",
    // Subscriptions on the notification connection, which starts at Elect.
    "PgWorkNotificationListener", "MultiStreamCommandSubscriber", "PartitionAssignmentSubscriber",
    // In-process state, or the transport's management plane rather than the database.
    "RecentlyProcessedEventCacheSweepWorker", "BacklogAgeWorker",
    // Fed only by a command receptor, and dispatch is deferred until the schema-ready gate opens.
    "RebuildBackgroundWorker",
    // The health-check publisher runs checks only for a publisher the application registers; the framework registers
    // none, and a check the application asks for is the application's to time.
    "HealthCheckPublisherHostedService",
    // Work done only when the host stops.
    "WhizbangShutdownService", "BatchStrategyShutdownFlush", "DutyShutdownReleaseService",
  };

  [Test]
  public async Task EveryHostedService_HasAPlaceInTheStageTable_AndTheStagedOnesStartAtTheirStageAsync() {
    using var host = _turnkeyHost();
    var hosted = host.Services.GetServices<IHostedService>().Select(_describe).ToList();

    var unplaced = hosted.Where(h => h.Stage is null && !_afterTheSchemaReadyGate.Contains(h.Name)
      && !_preflight.Contains(h.Name) && !_noDatabaseWorkAtStart.Contains(h.Name)).Select(h => h.Name).ToList();
    await Assert.That(unplaced).IsEmpty()
      .Because("every hosted service must say when it may first touch the database; unplaced: " + string.Join(", ", unplaced));

    var ungatedButStaged = hosted.Where(h => h.Stage is null && _atStage.ContainsKey(h.Name)).Select(h => h.Name).ToList();
    await Assert.That(ungatedButStaged).IsEmpty()
      .Because("these start at a stage, so registering them directly would start them with the host: " + string.Join(", ", ungatedButStaged));

    foreach (var (name, stage) in _atStage) {
      var registered = hosted.Where(h => h.Name == name && h.Stage is not null).ToList();
      await Assert.That(registered.Count).IsEqualTo(1).Because($"{name} is registered once, at its stage");
      await Assert.That(registered[0].Stage).IsEqualTo(stage).Because($"{name} starts at {stage}");
    }
  }

  private static (string Name, StartupStage? Stage) _describe(IHostedService service) => service switch {
    StartupStageGatedService<PgSharedNotifyConnection> gated => (nameof(PgSharedNotifyConnection), gated.Stage),
    StartupStageGatedService<SignalBusHostedService> gated => (nameof(SignalBusHostedService), gated.Stage),
    StartupStageGatedService<DutyHolderWorker> gated => (nameof(DutyHolderWorker), gated.Stage),
    StartupStageGatedService<PartitionAssignerWorker> gated => (nameof(PartitionAssignerWorker), gated.Stage),
    _ => (service.GetType().Name, null),
  };

  private static IHost _turnkeyHost() {
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:db"] = "Host=localhost;Database=audit;Username=audit;Password=audit",
    });
    builder.Services.AddSingleton<ITransport>(new InProcessTransport());
    _ = builder.Services.AddWhizbang().WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    return builder.Build();
  }
}
