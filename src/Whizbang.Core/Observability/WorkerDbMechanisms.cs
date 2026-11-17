// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

// The mechanism each framework worker's database statements are attributed to, in one table so the whole set reads
// at a glance. The host opens the scope around each worker's start (GatedHostedService); a worker hosted without one
// reports its statements as untagged, and DbMechanismWorkerScopeTests fails on any framework worker missing here.

using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers {
  public partial class HeartbeatWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Heartbeat;
  }

  public partial class ClaimWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Claim;
  }

  public partial class PerspectiveWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Perspective;
  }

  public partial class OutboxCompletionFlushWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.CompletionFlush;
  }

  public partial class PerspectiveCompletionFlushWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.CompletionFlush;
  }

  public partial class FailureFlushWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.CompletionFlush;
  }

  public partial class LeaseRenewalWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Lease;
  }

  public partial class InboxHandlerWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Inbox;
  }

  public partial class InboxDispatchWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Inbox;
  }

  public partial class InboxDrainWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Inbox;
  }

  public partial class MaintenanceWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Maintenance;
  }

  public partial class OrphanInboxJanitor : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Maintenance;
  }

  public partial class CoalesceShipWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Coalesce;
  }

  public partial class IntegrityCheckpointWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.IntegrityCheckpoint;
  }

  public partial class SubscriptionExpansionWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Subscription;
  }

  public partial class SubscriptionBackfillWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Subscription;
  }

  public partial class IntegrityAuditWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.IntegrityAudit;
  }

  public partial class RepairDrainWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.IntegrityRepair;
  }

  public partial class IntegrityLedgerRecheckWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.IntegrityRecheck;
  }

  public partial class OriginReattributionWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.OriginReattribution;
  }

  public partial class IntegrityDeliveryWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.IntegrityDelivery;
  }

  public partial class OutboxPublishWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Outbox;
  }

  public partial class OutboxDrainWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Outbox;
  }

  public partial class PartitionAssignerWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Role;
  }

  public partial class WhizbangShutdownService : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Lifecycle;
  }

  public partial class TransportConsumerWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Inbox;
  }

  public partial class BackupTickCoordinator : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Maintenance;
  }

  public partial class DeadLetterRecoveryWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.DeadLetter;
  }

  public partial class TransportDeadLetterDrainWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.DeadLetter;
  }
}

namespace Whizbang.Core.Temporal {
  public partial class ScheduleWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Schedule;
  }
}

namespace Whizbang.Core.Observability {
  public partial class NotifyDebounceStatsCollector : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.NotifyStatistics;
  }

  public partial class TableStatisticsCollector : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.TableStatistics;
  }

  public partial class EventTypeBytesSampler : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.TableStatistics;
  }

  public partial class PendingReclamationSampler : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.TableStatistics;
  }
}

namespace Whizbang.Core.Startup {
  public partial class DutyHolderWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Role;
  }

  public partial class StandbyWatcher : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Standby;
  }
}

namespace Whizbang.Core.Signals {
  public partial class SignalBusHostedService : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Signals;
  }
}

namespace Whizbang.Core.Fingerprint {
  public partial class TypeDefinitionReconcilerHostedService : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.TypeRegistry;
  }
}

namespace Whizbang.Core.MultiStream {
  public partial class MultiStreamCommandWorker : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.MultiStream;
  }
}

namespace Whizbang.Core.Configuration {
  internal partial class StartupSettingsReportService : IDbMechanismOwner {
    /// <inheritdoc />
    public DbMechanism DbMechanism => DbMechanism.Startup;
  }
}
