// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Transports;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Recovery, fleet, type registry, lifecycle and cursor reads on the Dapper driver: the stuck-row sentinels, the lease
/// releases, the service backlog, the broker dead-letter import, standby and eviction, the type-definition catalog and its
/// reconcile claim, the lifecycle completion marker and the orphan reconciliation, the cursor batch and rewind reads, the
/// ephemeral snapshot targets and drift count, and the local service id.
/// </summary>
/// <remarks>
/// This coordinator used to inherit the interface's inert defaults for every member here (#2051), so on this driver a
/// stuck row was never reported, a lease the instance gave up stayed held until it expired, the housekeeping gate read
/// the backlog as unmeasured, a broker dead letter was never taken into custody, a standby request went nowhere, the
/// type-definition reconciler saw an empty catalog, no lifecycle was ever reconciled, and a cursor flagged for a rewind
/// was never rewound. Each member now runs the statement the EF Core coordinator runs, from
/// <see cref="CoordinatorFleetSql"/>, <see cref="CoordinatorCatalogSql"/> or <see cref="OrphanedLifecycleEventsSql"/>,
/// qualified with this coordinator's schema. An empty input returns before a connection is opened.
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperFleetRecoveryTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperLifecycleAndCatalogTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorDriverParityTests.cs</tests>
public partial class DapperWorkCoordinator {
  /// <inheritdoc />
  public Task<IReadOnlyList<StuckRow>> FindStuckOutboxRowsAsync(int maxAttempts, int limit, CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) =>
      CoordinatorFleetSql.FindStuckRowsAsync(cmd, prefix, CoordinatorFleetSql.STUCK_OUTBOX, maxAttempts, limit, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<StuckRow>> FindStuckInboxRowsAsync(int maxAttempts, int limit, CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) =>
      CoordinatorFleetSql.FindStuckRowsAsync(cmd, prefix, CoordinatorFleetSql.STUCK_INBOX, maxAttempts, limit, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<int> DropExhaustedInboxRowsAsync(IReadOnlyList<Guid> messageIds, int maxAttempts, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(messageIds);
    if (messageIds.Count == 0) {
      return Task.FromResult(0);
    }
    return _withGatedCommandAsync((cmd, prefix) =>
      CoordinatorFleetSql.DropExhaustedInboxRowsAsync(cmd, prefix, messageIds, maxAttempts, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> ReleaseUnprocessedInboxAsync(Guid instanceId, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(messageIds);
    if (messageIds.Count == 0) {
      return Task.FromResult(0);
    }
    return _withGatedCommandAsync((cmd, prefix) =>
      CoordinatorFleetSql.ReleaseUnprocessedInboxAsync(cmd, prefix, instanceId, messageIds, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Calls <c>release_unstarted_leases</c> (migration 145) through the statement EF Core runs too: the claim worker's
  /// #724 release and the run-control path's held-work step (#2045) behave the same on both drivers. Two empty lists
  /// cost no round trip.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperWorkCoordinatorGuardAndGateTests.cs:ReleaseUnstartedLeasesAsync_ReleasesThisInstancesUnprocessedInboxLease_AndEndsItsStreamOwnershipAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperFleetRecoveryTests.cs</tests>
  public Task<UnstartedLeaseRelease> ReleaseUnstartedLeasesAsync(
      Guid instanceId, IReadOnlyList<Guid> inboxStreamIds, IReadOnlyList<Guid> perspectiveStreamIds,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(inboxStreamIds);
    ArgumentNullException.ThrowIfNull(perspectiveStreamIds);
    if (inboxStreamIds.Count == 0 && perspectiveStreamIds.Count == 0) {
      return Task.FromResult(new UnstartedLeaseRelease(0, 0));
    }
    return _withGatedCommandAsync((cmd, prefix) => CoordinatorFleetSql.ReleaseUnstartedLeasesAsync(
      cmd, prefix, instanceId, inboxStreamIds, perspectiveStreamIds, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public async ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) =>
    await _withGatedCommandAsync((cmd, prefix) => CoordinatorFleetSql.CountServiceBacklogAsync(cmd, prefix, cancellationToken), cancellationToken)
      .ConfigureAwait(false);

  /// <inheritdoc />
  /// <remarks>
  /// A failed import throws, never returns false: false means the custody already existed, which the drainer settles at
  /// the broker, so a failure reported as false would lose the message.
  /// </remarks>
  public async Task<bool> ImportBrokerDeadLetterAsync(BrokerDeadLetterImport import, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(import);
    try {
      return await _withGatedCommandAsync((cmd, prefix) => CoordinatorFleetSql.ImportBrokerDeadLetterAsync(
        cmd, prefix, import, (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New(), (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New(),
        new DefaultGenerationProvider().GetGeneration(), cancellationToken), cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      throw CoordinatorFleetSql.ImportFailed(import, ex);
    }
  }

  /// <inheritdoc />
  public async Task<bool> RequestStandbyAsync(Guid instanceId, string version, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(version);
    return await _withGatedCommandAsync((cmd, prefix) => CoordinatorFleetSql.ScalarFunctionAsync(
      cmd, prefix, "request_standby", cancellationToken, ("id", instanceId), ("version", version)), cancellationToken).ConfigureAwait(false) is true;
  }

  /// <inheritdoc />
  public async Task<bool> ClearStandbyRequestAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
    await _withGatedCommandAsync((cmd, prefix) => CoordinatorFleetSql.ScalarFunctionAsync(
      cmd, prefix, "clear_standby", cancellationToken, ("id", instanceId)), cancellationToken).ConfigureAwait(false) is true;

  /// <inheritdoc />
  public Task EvictInstanceAsync(Guid instanceId, Guid evictedBy, string reason, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    return _withGatedCommandAsync((cmd, prefix) => CoordinatorFleetSql.ScalarFunctionAsync(
      cmd, prefix, "evict_instance", cancellationToken, ("id", instanceId), ("by", evictedBy), ("reason", reason)), cancellationToken);
  }

  /// <inheritdoc />
  public Task<StandbyRequest?> GetStandbyRequestAsync(CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) => CoordinatorFleetSql.GetStandbyRequestAsync(cmd, prefix, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<TypeDefinitionInfo>> GetTypeDefinitionsAsync(CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) => CoordinatorCatalogSql.TypeDefinitionsAsync(cmd, prefix, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<TypeDefinitionRegistration> RegisterTypeDefinitionAsync(
      string eventTypeName, string settingsHashHex, string schemaHashHex, int schemaVersion, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypeName);
    return _withGatedCommandAsync((cmd, prefix) => CoordinatorCatalogSql.RegisterTypeDefinitionAsync(
      cmd, prefix, eventTypeName, settingsHashHex, schemaHashHex, schemaVersion, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task RecordDefinitionLineageAsync(
      int fromDefinitionId, int toDefinitionId, DefinitionRelationship relationship, string? migrationRef,
      CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync(async (cmd, prefix) => {
      await CoordinatorCatalogSql.RecordDefinitionLineageAsync(
        cmd, prefix, fromDefinitionId, toDefinitionId, relationship, migrationRef, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<bool> TryClaimTypeDefinitionReconcileAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) => PerspectiveRowRetentionSql.TryClaimWatermarkAsync(
      cmd, prefix, CoordinatorCatalogSql.TYPE_DEFINITION_RECONCILE_KEY, CoordinatorCatalogSql.TYPE_DEFINITION_RECONCILE_DESCRIPTION,
      claimWindow, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<long> CountSourcedEventsForTypesAsync(IReadOnlyList<string> eventTypeNames, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypeNames);
    if (eventTypeNames.Count == 0) {
      return Task.FromResult(0L);
    }
    return _withGatedCommandAsync((cmd, prefix) =>
      CoordinatorCatalogSql.CountSourcedEventsForTypesAsync(cmd, prefix, eventTypeNames, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<EphemeralSnapshotTarget>> GetEphemeralPairsNeedingSnapshotAsync(CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) => CoordinatorCatalogSql.EphemeralPairsNeedingSnapshotAsync(cmd, prefix, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(Guid[] streamIds, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    if (streamIds.Length == 0) {
      return Task.FromResult<List<PerspectiveCursorInfo>>([]);
    }
    return _withGatedCommandAsync((cmd, prefix) =>
      CoordinatorCatalogSql.PerspectiveCursorsBatchAsync(cmd, prefix, streamIds, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<RewindCursorInfo>> GetCursorsRequiringRewindAsync(CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) => CoordinatorCatalogSql.CursorsRequiringRewindAsync(cmd, prefix, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) => CoordinatorCatalogSql.LocalServiceIdAsync(cmd, prefix, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task RecordLifecycleCompletionAsync(Guid eventId, CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync(async (cmd, prefix) => {
      await OrphanedLifecycleEventsSql.RecordCompletionAsync(cmd, prefix, eventId, (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New(), cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);

  /// <inheritdoc />
  /// <remarks>
  /// One set-based pass over every registered type, globally capped, oldest first; the caller loops bounded passes. A
  /// failed query is logged and answers what it found (nothing), and an event whose body cannot be read is logged and
  /// skipped, so the reconciliation never stops the host.
  /// </remarks>
  public async Task<IReadOnlyList<OrphanedLifecycleEvent>> GetOrphanedLifecycleEventsAsync(
      Dictionary<string, IReadOnlyList<string>> perspectivesPerEventType, TimeSpan lookbackWindow, int maxOrphans = 100,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(perspectivesPerEventType);
    var (types, names) = OrphanedLifecycleEventsSql.Pairs(perspectivesPerEventType);
    if (types.Length == 0) {
      return [];
    }
    var start = DateTimeOffset.UtcNow - lookbackWindow;
    IReadOnlyList<OrphanedLifecycleEventsSql.Row> rows;
    try {
      rows = await _withGatedCommandAsync((cmd, prefix) =>
        OrphanedLifecycleEventsSql.ReadAsync(cmd, prefix, types, names, start, maxOrphans, cancellationToken), cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogOrphanedLifecycleQueryFailed(_log, ex, perspectivesPerEventType.Count);
      return [];
    }
    var orphaned = new List<OrphanedLifecycleEvent>(rows.Count);
    foreach (var row in rows) {
      try {
        var envelope = OrphanedLifecycleEventsSql.ToEnvelope(row.EventId, row.EventData, row.Scope, _jsonOptions, _reportScopeUnreadable);
        orphaned.Add(new OrphanedLifecycleEvent(row.EventId, row.StreamId, envelope));
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogOrphanedLifecycleEventUnreadable(_log, ex, row.EventId, row.EventType);
      }
    }
    return orphaned;
  }

  private void _reportScopeUnreadable(Exception ex) => LogReconcileScopeUnreadable(_log, ex);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to query orphaned lifecycle events ({TypeCount} type(s))")]
  static partial void LogOrphanedLifecycleQueryFailed(ILogger logger, Exception ex, int typeCount);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to deserialize orphaned event {EventId} (type: {EventType}) for reconciliation")]
  static partial void LogOrphanedLifecycleEventUnreadable(ILogger logger, Exception ex, Guid eventId, string eventType);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Failed to parse reconciliation scope JSON; the replayed lifecycle event will run without its stored scope.")]
  static partial void LogReconcileScopeUnreadable(ILogger logger, Exception ex);
}
