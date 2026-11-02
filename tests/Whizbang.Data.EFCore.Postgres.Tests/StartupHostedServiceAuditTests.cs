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
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every hosted service a turnkey host registers says when it may first touch the database (#1636). The host is
/// composed the way a consumer composes one (the EF Core Postgres driver, its notification stack, the worker pipeline,
/// a transport and its consumer) and built, not started; each hosted service it resolves must be one of:
/// <list type="bullet">
///   <item>a worker hosted with its start point (<c>AddWhizbangWorker</c>, <c>AddWhizbangWorkerAtSchemaReady</c>), at
///     the start point this table names: the data plane and the transport consumer at Accept commands, so nothing
///     claims or receives before the instance's row says it is eligible for work; the heartbeat and the work that keeps
///     an instance alive and coordinated when the schema-ready gate opens;</item>
///   <item>schema initialization and the startup machinery itself, which drive the stages;</item>
///   <item>a service that does no database work when it starts (in-memory registration, configuration checks, a
///     subscription on the notification connection, or work done only on stop).</item>
/// </list>
/// </summary>
/// <remarks>
/// A hosted service added without a place in this table fails here, naming it, so the author has to decide its start
/// point rather than starting it with the host by default. The list is the composition's own answer, the check on the
/// source search that produced the table.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Startup/WhizbangWorkerServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageGatedService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Signals/SignalBusServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerBuilderExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs</code-under-test>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
[Category("Startup")]
[Category("Shard5")]
public sealed class StartupHostedServiceAuditTests {
  private const string ACCEPT_COMMANDS = "Accept commands";
  private const string SCHEMA_READY = "schema ready";

  /// <summary>The services hosted with a start point, and the start point.</summary>
  private static readonly Dictionary<string, string> _startPoints = new(StringComparer.Ordinal) {
    // Elect and Coordinate (#1849, #1989).
    ["PgSharedNotifyConnection"] = "Elect",
    ["SignalBusHostedService"] = "Elect",
    ["DutyHolderWorker"] = "Coordinate",
    ["PartitionAssignerWorker"] = "Coordinate",
    // Alive and coordinated, not yet eligible for work: when the schema-ready gate opens, at the end of Coordinate.
    ["HeartbeatWorker"] = SCHEMA_READY,
    ["PgInstanceLifecycleMonitor"] = SCHEMA_READY,
    ["StandbyWatcher"] = SCHEMA_READY,
    ["TypeDefinitionReconcilerHostedService"] = SCHEMA_READY,
    ["TableStatisticsCollector"] = SCHEMA_READY,
    ["EventTypeBytesSampler"] = SCHEMA_READY,
    ["NotifyDebounceStatsCollector"] = SCHEMA_READY,
    // Deregistration is the stop of a service that started when the schema was ready (#1991): an instance stopped
    // before then has nothing to deregister, against tables that may not exist yet.
    ["WhizbangShutdownService"] = SCHEMA_READY,
    // The transport consumer receives while the read models catch up (owner decision, 2026-11-02).
    ["TransportConsumerWorker"] = ACCEPT_COMMANDS,
    // The data plane: nothing claims, drains, flushes or schedules before the row records Accept commands.
    ["BackupTickCoordinator"] = ACCEPT_COMMANDS,
    ["ClaimWorker"] = ACCEPT_COMMANDS,
    ["CoalesceShipWorker"] = ACCEPT_COMMANDS,
    ["DeadLetterRecoveryWorker"] = ACCEPT_COMMANDS,
    ["FailureFlushWorker"] = ACCEPT_COMMANDS,
    ["InboxDispatchWorker"] = ACCEPT_COMMANDS,
    ["InboxDrainWorker"] = ACCEPT_COMMANDS,
    ["InboxHandlerWorker"] = ACCEPT_COMMANDS,
    ["IntegrityAuditWorker"] = ACCEPT_COMMANDS,
    ["IntegrityCheckpointWorker"] = ACCEPT_COMMANDS,
    ["IntegritySweepScheduler"] = ACCEPT_COMMANDS,
    ["LeaseRenewalWorker"] = ACCEPT_COMMANDS,
    ["MaintenanceWorker"] = ACCEPT_COMMANDS,
    ["MultiStreamCommandWorker"] = ACCEPT_COMMANDS,
    ["OutboxCompletionFlushWorker"] = ACCEPT_COMMANDS,
    ["OutboxDrainWorker"] = ACCEPT_COMMANDS,
    ["OutboxPublishWorker"] = ACCEPT_COMMANDS,
    ["PerspectiveCompletionFlushWorker"] = ACCEPT_COMMANDS,
    ["PerspectiveWorker"] = ACCEPT_COMMANDS,
    ["PgCommitOrderStamperWorker"] = ACCEPT_COMMANDS,
    ["PgDurableSignalRetentionWorker"] = ACCEPT_COMMANDS,
    ["PgDurableSignalTailWorker"] = ACCEPT_COMMANDS,
    ["RepairDrainWorker"] = ACCEPT_COMMANDS,
    ["ScheduleWorker"] = ACCEPT_COMMANDS,
    ["SubscriptionExpansionWorker"] = ACCEPT_COMMANDS,
    ["TransportDeadLetterDrainWorker"] = ACCEPT_COMMANDS,
  };

  /// <summary>Schema initialization and the machinery that drives and reports the stages.</summary>
  private static readonly HashSet<string> _startupMachinery = new(StringComparer.Ordinal) {
    "WhizbangDatabaseInitializerService", "StartupPipelineWorker", "StartupStageHostLifecycle", "StartupReadyService",
    // Drive Accept commands and Running from the schema-ready and read-model gates: the transitions the workers wait for.
    "LifecyclePhaseWorker", "ReadModelsReadyDriver",
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
    "ScheduledIntegritySweepReceptorRegistrar", "SagaWatchdogTickRouterRegistrar", "DefaultBackupTickRegistrar",
    "IdleActivityTouchHookBinder",
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
    "BatchStrategyShutdownFlush", "DutyShutdownReleaseService",
  };

  [Test]
  public async Task EveryHostedService_HasAPlaceInTheStageTable_AndTheStagedOnesStartAtTheirStageAsync() {
    using var host = _turnkeyHost();
    var hosted = host.Services.GetServices<IHostedService>().Select(_describe).ToList();

    var unplaced = hosted.Where(h => h.StartPoint is null && !_startupMachinery.Contains(h.Name)
      && !_noDatabaseWorkAtStart.Contains(h.Name)).Select(h => h.Name).ToList();
    await Assert.That(unplaced).IsEmpty()
      .Because("every hosted service must say when it may first touch the database: register a worker with "
        + "AddWhizbangWorker or AddWhizbangWorkerAtSchemaReady; unplaced: " + string.Join(", ", unplaced));

    var wrongPoint = new List<string>();
    foreach (var (name, startPoint) in _startPoints) {
      var registered = hosted.Where(h => h.Name == name).ToList();
      if (registered.Count != 1 || registered[0].StartPoint != startPoint) {
        wrongPoint.Add($"{name}: expected once at {startPoint}, found [{string.Join(", ", registered.Select(r => r.StartPoint ?? "with the host"))}]");
      }
    }
    await Assert.That(wrongPoint).IsEmpty().Because(string.Join("; ", wrongPoint));
  }

  private static (string Name, string? StartPoint) _describe(IHostedService service) => service is GatedHostedService gated
    ? (gated.Service.GetType().Name, gated.StartPoint)
    : (service.GetType().Name, null);

  private static IHost _turnkeyHost() {
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:db"] = "Host=localhost;Database=audit;Username=audit;Password=audit",
    });
    builder.Services.AddSingleton<ITransport>(new InProcessTransport());
    _ = builder.Services.AddWhizbang().WithRouting(static r => r.OwnDomains("audit.commands"))
      .WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres.AddTransportConsumer();
    return builder.Build();
  }
}
