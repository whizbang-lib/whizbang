// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The mechanism each framework worker in Whizbang.Core is attributed to. The table is the decision; a worker moved to
/// another mechanism changes it here on purpose. The workers are read without being constructed, because only the
/// name is under test.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/WorkerDbMechanisms.cs</code-under-test>
/// <docs>operations/observability/database-attribution</docs>
[Category("Observability")]
public class WorkerDbMechanismsTests {
  private static readonly Dictionary<Type, DbMechanism> _expected = new() {
    [typeof(Whizbang.Core.Workers.HeartbeatWorker)] = DbMechanism.Heartbeat,
    [typeof(Whizbang.Core.Workers.ClaimWorker)] = DbMechanism.Claim,
    [typeof(Whizbang.Core.Workers.PerspectiveWorker)] = DbMechanism.Perspective,
    [typeof(Whizbang.Core.Workers.OutboxCompletionFlushWorker)] = DbMechanism.CompletionFlush,
    [typeof(Whizbang.Core.Workers.PerspectiveCompletionFlushWorker)] = DbMechanism.CompletionFlush,
    [typeof(Whizbang.Core.Workers.FailureFlushWorker)] = DbMechanism.CompletionFlush,
    [typeof(Whizbang.Core.Workers.LeaseRenewalWorker)] = DbMechanism.Lease,
    [typeof(Whizbang.Core.Workers.InboxHandlerWorker)] = DbMechanism.Inbox,
    [typeof(Whizbang.Core.Workers.InboxDispatchWorker)] = DbMechanism.Inbox,
    [typeof(Whizbang.Core.Workers.InboxDrainWorker)] = DbMechanism.Inbox,
    [typeof(Whizbang.Core.Workers.MaintenanceWorker)] = DbMechanism.Maintenance,
    [typeof(Whizbang.Core.Workers.OrphanInboxJanitor)] = DbMechanism.Maintenance,
    [typeof(Whizbang.Core.Workers.CoalesceShipWorker)] = DbMechanism.Coalesce,
    [typeof(Whizbang.Core.Workers.IntegrityCheckpointWorker)] = DbMechanism.IntegrityCheckpoint,
    [typeof(Whizbang.Core.Workers.SubscriptionExpansionWorker)] = DbMechanism.Subscription,
    [typeof(Whizbang.Core.Workers.SubscriptionBackfillWorker)] = DbMechanism.Subscription,
    [typeof(Whizbang.Core.Workers.IntegrityAuditWorker)] = DbMechanism.IntegrityAudit,
    [typeof(Whizbang.Core.Workers.RepairDrainWorker)] = DbMechanism.IntegrityRepair,
    [typeof(Whizbang.Core.Workers.IntegrityLedgerRecheckWorker)] = DbMechanism.IntegrityRecheck,
    [typeof(Whizbang.Core.Workers.OriginReattributionWorker)] = DbMechanism.OriginReattribution,
    [typeof(Whizbang.Core.Workers.IntegrityDeliveryWorker)] = DbMechanism.IntegrityDelivery,
    [typeof(Whizbang.Core.Workers.OutboxPublishWorker)] = DbMechanism.Outbox,
    [typeof(Whizbang.Core.Workers.OutboxDrainWorker)] = DbMechanism.Outbox,
    [typeof(Whizbang.Core.Workers.PartitionAssignerWorker)] = DbMechanism.Role,
    [typeof(Whizbang.Core.Workers.WhizbangShutdownService)] = DbMechanism.Lifecycle,
    [typeof(Whizbang.Core.Workers.TransportConsumerWorker)] = DbMechanism.Inbox,
    [typeof(Whizbang.Core.Workers.BackupTickCoordinator)] = DbMechanism.Maintenance,
    [typeof(Whizbang.Core.Workers.DeadLetterRecoveryWorker)] = DbMechanism.DeadLetter,
    [typeof(Whizbang.Core.Workers.TransportDeadLetterDrainWorker)] = DbMechanism.DeadLetter,
    [typeof(Whizbang.Core.Temporal.ScheduleWorker)] = DbMechanism.Schedule,
    [typeof(Whizbang.Core.Observability.NotifyDebounceStatsCollector)] = DbMechanism.NotifyStatistics,
    [typeof(Whizbang.Core.Observability.TableStatisticsCollector)] = DbMechanism.TableStatistics,
    [typeof(Whizbang.Core.Observability.EventTypeBytesSampler)] = DbMechanism.TableStatistics,
    [typeof(Whizbang.Core.Observability.PendingReclamationSampler)] = DbMechanism.TableStatistics,
    [typeof(Whizbang.Core.Startup.DutyHolderWorker)] = DbMechanism.Role,
    [typeof(Whizbang.Core.Startup.StandbyWatcher)] = DbMechanism.Standby,
    [typeof(Whizbang.Core.Signals.SignalBusHostedService)] = DbMechanism.Signals,
    [typeof(Whizbang.Core.Fingerprint.TypeDefinitionReconcilerHostedService)] = DbMechanism.TypeRegistry,
    [typeof(Whizbang.Core.MultiStream.MultiStreamCommandWorker)] = DbMechanism.MultiStream,
    [typeof(Whizbang.Core.Configuration.StartupSettingsReportService)] = DbMechanism.Startup,
  };

  [Test]
  public async Task EachWorker_IsAttributedToItsMechanismAsync() {
    foreach (var (type, mechanism) in _expected) {
      var worker = (IDbMechanismOwner)RuntimeHelpers.GetUninitializedObject(type);
      await Assert.That(worker.DbMechanism).IsSameReferenceAs(mechanism).Because(type.Name);
    }
  }
}
