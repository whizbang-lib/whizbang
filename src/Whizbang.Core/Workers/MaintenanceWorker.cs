using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Periodic timer that calls <see cref="IWorkCoordinator.PerformMaintenanceAsync"/> on a
/// configurable interval. Mirrors the legacy publisher's <c>_runPeriodicMaintenanceAsync</c>
/// (lines 476-488) — same behavior, decoupled from the polling loop.
/// </summary>
/// <remarks>
/// Maintenance is a no-op on most engines that ship default <c>PerformMaintenanceAsync</c>
/// (returns an empty list). Engines with active housekeeping (stale-instance purge,
/// dead-letter cleanup, dedup pruning) light up automatically.
/// </remarks>
/// <docs>fundamentals/work-coordinator/maintenance</docs>
public sealed partial class MaintenanceWorker(
  IServiceScopeFactory scopeFactory,
  ISchemaReadyGate schemaReadyGate,
  IOptions<MaintenanceWorkerOptions> options,
  ILogger<MaintenanceWorker> logger,
  Whizbang.Core.Observability.MaintenanceMetrics? metrics = null) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly MaintenanceWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<MaintenanceWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.IntervalMinutes);

    if (!_options.Enabled) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
      return;
    }

    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
    } catch (OperationCanceledException) {
      return;
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await RunMaintenanceOnceAsync(stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogError(_logger, ex);
      }

      try {
        await Task.Delay(TimeSpan.FromMinutes(_options.IntervalMinutes), stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }

    LogStopped(_logger);
  }

  internal async Task RunMaintenanceOnceAsync(CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var sp = scope.ServiceProvider;
    var coordinator = sp.GetRequiredService<IWorkCoordinator>();

    // Reap-driven ephemeral snapshots — run BEFORE perform_maintenance so a snapshot rewind-floor exists for
    // every (stream, perspective) whose consumed, aged-past-grace ephemeral bodies the reaper (Task 8) is
    // about to delete. This is what makes the coverage gate safe on low-volume / idle streams.
    await _reapDrivenEphemeralSnapshotAsync(coordinator, sp, ct);

    // E2 PreDestruction hooks — run BEFORE the reap so a registered IDestructionHook can preserve / compact /
    // archive each ephemeral body before Task 8 deletes it. Its preserve-work commits before the subsequent
    // PerformMaintenanceAsync (a separate call), satisfying the "durable before delete" ordering. Returns the
    // targets so PostDestruction can fire after the delete. Inert without a registered hook.
    var destructionTargets = await _runPreDestructionHooksAsync(coordinator, sp, ct);

    // Stream-integrity convergence gauges. Deliberately folded into THIS cycle rather than given
    // their own timer: a standalone collector would be one more background service acquiring a
    // connection on a schedule in every host, and small pools feel that immediately (the sample
    // harnesses run with Max Pool Size 2). This rides a scope and a cadence that already exist, so
    // it costs one extra query rather than a new periodic connection. Best-effort — a metrics read
    // must never fail a maintenance cycle.
    var integrity = sp.GetService<Microsoft.Extensions.Options.IOptions<Whizbang.Core.Messaging.StreamIntegrityOptions>>()?.Value;
    try {
      var integrityMetrics = sp.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
      if (integrity is not null && integrityMetrics is not null) {
        integrityMetrics.UpdateLedgerGauges(
          await coordinator.GetIntegrityLedgerSummaryAsync(integrity.MaxRepairAttemptsPerBucket, ct));
      }
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogLedgerGaugeRefreshFailed(_logger, ex);
    }

    // Digest-epoch closure (migration 092). The substrate is inert without a caller — nothing
    // else advances the frontier, and an unclosed frontier means manifests keep re-aggregating
    // live history. The settle window is the AUDIT's settle window on purpose: closure and the
    // audit must agree on what "settled" means, or a seal could disagree with a manifest folded
    // over the same range. Best-effort — the frontier just advances on a later cycle.
    if (integrity is { EpochClosureEnabled: true }) {
      try {
        var closed = await coordinator.CloseDigestEpochsAsync(
          integrity.AuditSettleWindowMinutes * 60, integrity.MaxEpochClosuresPerMaintenanceCycle, ct);
        if (closed > 0) {
          LogEpochsClosed(_logger, closed);
        }
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        LogEpochClosureFailed(_logger, ex);
      }
    }

    var results = await coordinator.PerformMaintenanceAsync(ct);
    foreach (var r in results) {
      LogMaintenanceResult(_logger, r.TaskName, r.RowsAffected, r.DurationMs);
      // Fleet-visible per-task outcomes (row retention's "is it working" signal rides
      // task=reap_expired_perspective_rows). Optional — null when metrics aren't registered.
      metrics?.Record(r.TaskName, r.RowsAffected, r.DurationMs);
    }

    // E2 PostDestruction hooks — detached, after the reap committed.
    await _firePostDestructionHooksAsync(sp, destructionTargets, ct);

    // Tier-2 deep maintenance (E1 #13b3): prune ancient ephemeral pointers. The backing SQL self-gates on
    // the opt-in flag (disabled by default) and a ~monthly interval, so this per-cycle call is a cheap
    // no-op when disabled or not due. Best-effort: a prune failure is logged and never fails the cycle.
    try {
      var prune = await coordinator.PruneAncientEphemeralPointersAsync(ct);
      if (prune.RowsPruned > 0) {
        LogPointerPrune(_logger, prune.RowsPruned);
      }
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogPointerPruneFailed(_logger, ex);
    }

    // Detect space held but unusable — churn leaves free space inside pages that autovacuum
    // returns to the free space map but never to the OS, and a dropped column leaves bytes that
    // autovacuum can never reclaim at all. Detection RECORDS the rewrite request; the post-ready
    // Rewrite startup step performs it, in the window it should always have had.
    await _rewriteBloatedTablesAsync(coordinator, ct);

    // v0.657 slice 5: structural stuck-row sentinel. Runs after the regular
    // maintenance cycle so backings that don't implement it (default no-op
    // returning empty list) pay zero cost; Postgres backends use the partial
    // indexes added in migration 054 for O(log N) scan on a ~0-sized index.
    if (_options.StuckRowSentinelEnabled) {
      await _runStuckRowSentinelAsync(coordinator, ct);
    }
  }

  // Snapshots each (stream, perspective) the reaper is about to strand, using the runner's bootstrap hook.
  // No-op without a runner registry (non-perspective host) or without ephemeral bodies. Per-pair failures
  // are logged, never fatal — a snapshot failure just leaves the body for a later cycle (the coverage gate
  // holds it), it never loses data.

  /// <summary>
  /// Detects tables holding space they cannot use, and RECORDS that a rewrite is owed — it never
  /// performs one. Executing a rewrite takes an ACCESS EXCLUSIVE lock, and the runtime
  /// maintenance cadence was always the wrong window for that; the post-ready <c>Rewrite</c>
  /// startup step performs the recorded requests under the maintainer duty, in the window they
  /// should always have had (see <c>TableRewriteStartupStep</c>).
  /// </summary>
  /// <remarks>
  /// Candidates come from <c>wh_tables_needing_rewrite()</c>, which re-measures each one, so an
  /// already-rewritten table yields nothing. Recording is what makes the next boot's step pick
  /// the table up deterministically instead of depending on the bloat still measuring over
  /// threshold at that moment; a candidate already recorded is not re-recorded.
  /// </remarks>
  private async Task _rewriteBloatedTablesAsync(IWorkCoordinator coordinator, CancellationToken ct) {
    IReadOnlyList<Whizbang.Core.Messaging.TableRewriteCandidate> candidates;
    try {
      candidates = await coordinator.GetTablesNeedingRewriteAsync(ct);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogRewriteScanFailed(_logger, ex);
      return;
    }

    foreach (var c in candidates) {
      if (ct.IsCancellationRequested) {
        return;
      }
      // Report either way — an operator who never looks at this still gets the bloat gauge.
      LogRewriteNeeded(_logger, c.TableName, c.BloatRatio);
      if (c.Requested) {
        continue;   // already on the books
      }
      try {
        await coordinator.RequestTableRewriteAsync(c.TableName, ct);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        LogRewriteFailed(_logger, c.TableName, ex);
      }
    }
  }

  private async Task _reapDrivenEphemeralSnapshotAsync(IWorkCoordinator coordinator, IServiceProvider sp, CancellationToken ct) {
    var registry = sp.GetService<Whizbang.Core.Perspectives.IPerspectiveRunnerRegistry>();
    if (registry is null) {
      return;
    }
    var targets = await coordinator.GetEphemeralPairsNeedingSnapshotAsync(ct);
    foreach (var target in targets) {
      ct.ThrowIfCancellationRequested();
      var runner = registry.GetRunner(target.PerspectiveName, sp);
      if (runner is null) {
        continue;
      }
      try {
        await runner.BootstrapSnapshotAsync(target.StreamId, target.PerspectiveName, target.LastEventId, ct);
        LogReapDrivenSnapshot(_logger, target.PerspectiveName, target.StreamId);
      } catch (Exception ex) {
        LogReapDrivenSnapshotFailed(_logger, ex, target.PerspectiveName, target.StreamId);
      }
    }
  }

  // E2 PreDestruction: fire the registered IDestructionHook for each ephemeral body about to be reaped this
  // cycle, BEFORE the reap. Inert without a hook (returns empty → today's blunt reap). Per-target failures
  // are logged, never fatal (the retry policy lands in a later increment). Returns the targets so the
  // detached PostDestruction step can fire after the reap. The hook's Cancel/Defer decision is captured and
  // logged; ENFORCEMENT (holding the body / rescheduling) lands in the next increment via a SQL hold-gate.
  private async Task<IReadOnlyList<Whizbang.Core.Messaging.EphemeralDestructionTarget>> _runPreDestructionHooksAsync(
      IWorkCoordinator coordinator, IServiceProvider sp, CancellationToken ct) {
    var hook = sp.GetService<Whizbang.Core.Lifecycle.IDestructionHook>();
    if (hook is null) {
      return [];
    }
    var targets = await coordinator.GetEphemeralBodiesAboutToReapAsync(ct);
    if (targets.Count == 0) {
      return targets;
    }
    // Batched: fire the hook ONCE for the whole set of about-to-reap events, then HONOR its decision.
    try {
      var result = await hook.OnBeforeDestructionAsync(_destructionContext(targets), ct).ConfigureAwait(false);
      LogPreDestruction(_logger, targets.Count, result.Cancel, result.DeferUntil.HasValue);
      if (result.Cancel || result.DeferUntil.HasValue) {
        // E2-3: hold the whole batch — Cancel keeps it far-future, Defer holds it to that instant. Task 8
        // skips held bodies, so nothing is reaped this cycle and PostDestruction does NOT fire (return []).
        var eventIds = new List<Guid>(targets.Count);
        foreach (var t in targets) {
          eventIds.Add(t.EventId);
        }
        var until = result.DeferUntil ?? DateTimeOffset.MaxValue;
        await coordinator.HoldEphemeralDestructionAsync(eventIds, until, ct).ConfigureAwait(false);
        return [];
      }
      // Proceed: no hold — the reap deletes the batch, and PostDestruction fires for it.
      return targets;
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      // E2-5: a throwing PreDestruction hook no longer fails open. The batch is HELD for a backoff (re-offered
      // to the hook next cycle) up to MaxDestructionRetries; past that a FORCED delete lets the reap proceed,
      // so a permanently-broken hook can never leak storage. RecordDestructionFailureAsync increments the
      // batch's attempt count and returns its highest; on engines without the hold infra it returns int.MaxValue
      // (⇒ forced delete ⇒ the prior fail-open behaviour). No PostDestruction fires on failure.
      var eventIds = new List<Guid>(targets.Count);
      foreach (var t in targets) {
        eventIds.Add(t.EventId);
      }
      var retryUntil = DateTimeOffset.UtcNow.AddSeconds(_options.DestructionRetryBackoffSeconds);
      var policy = _options.OnDestroyFailure;
      var attempt = await coordinator.RecordDestructionFailureAsync(
        eventIds, retryUntil, _options.MaxDestructionRetries, policy, ct).ConfigureAwait(false);
      var exhausted = attempt > _options.MaxDestructionRetries;
      var outcome = policy switch {
        Lifecycle.OnDestroyFailure.ForceDeleteImmediately => "FORCED DELETE (policy=ForceDeleteImmediately)",
        Lifecycle.OnDestroyFailure.RetryThenKeep when exhausted => "KEPT (retries exhausted, policy=RetryThenKeep)",
        _ when exhausted => "FORCED DELETE (retries exhausted)",
        _ => "held for retry",
      };
      LogPreDestructionFailed(_logger, ex, targets.Count, attempt, _options.MaxDestructionRetries, outcome);
      return [];
    }
  }

  // E2 PostDestruction: fire the registered IDestructionHook's detached post-hook ONCE for the reaped batch,
  // after the reap committed. Failure is logged, never fatal.
  private async Task _firePostDestructionHooksAsync(
      IServiceProvider sp, IReadOnlyList<Whizbang.Core.Messaging.EphemeralDestructionTarget> targets, CancellationToken ct) {
    if (targets.Count == 0) {
      return;
    }
    var hook = sp.GetService<Whizbang.Core.Lifecycle.IDestructionHook>();
    if (hook is null) {
      return;
    }
    try {
      await hook.OnAfterDestructionAsync(_destructionContext(targets), ct).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogPostDestructionFailed(_logger, ex, targets.Count);
    }
  }

  private static Whizbang.Core.Lifecycle.DestructionContext _destructionContext(
      IReadOnlyList<Whizbang.Core.Messaging.EphemeralDestructionTarget> targets) =>
    new() {
      Reason = Whizbang.Core.Lifecycle.DestructionReason.ConsumptionComplete,
      Granularity = Whizbang.Core.Lifecycle.DestructionGranularity.Event,
      Targets = targets,
    };

  private async Task _runStuckRowSentinelAsync(IWorkCoordinator coordinator, CancellationToken ct) {
    var max = _options.StuckRowSentinelMaxAttempts;
    var limit = _options.StuckRowSentinelLimit;
    var stuckOutbox = await coordinator.FindStuckOutboxRowsAsync(max, limit, ct);
    foreach (var row in stuckOutbox) {
      LogStuckOutboxRow(_logger, row.MessageId, row.MessageType, row.StreamId, row.Attempts, row.ClaimedSince);
    }
    var stuckInbox = await coordinator.FindStuckInboxRowsAsync(max, limit, ct);
    foreach (var row in stuckInbox) {
      LogStuckInboxRow(_logger, row.MessageId, row.MessageType, row.StreamId, row.Attempts, row.ClaimedSince);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "MaintenanceWorker started: intervalMinutes={IntervalMinutes}")]
  static partial void LogStarted(ILogger logger, int intervalMinutes);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "MaintenanceWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "MaintenanceWorker disabled via options — maintenance skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "MaintenanceWorker tick failed; will retry on next interval")]
  static partial void LogError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 20, Level = LogLevel.Debug,
    Message = "Reap-driven ephemeral snapshot for perspective {Perspective} on stream {StreamId}")]
  static partial void LogReapDrivenSnapshot(ILogger logger, string perspective, Guid streamId);

  [LoggerMessage(EventId = 21, Level = LogLevel.Warning,
    Message = "Reap-driven ephemeral snapshot failed for perspective {Perspective} on stream {StreamId} (non-fatal — body held for a later cycle)")]
  static partial void LogReapDrivenSnapshotFailed(ILogger logger, Exception ex, string perspective, Guid streamId);

  [LoggerMessage(EventId = 22, Level = LogLevel.Information,
    Message = "Tier-2 deep maintenance pruned {RowsPruned} ancient ephemeral event-store pointers (newest pointer per stream retained)")]
  static partial void LogPointerPrune(ILogger logger, long rowsPruned);

  [LoggerMessage(EventId = 23, Level = LogLevel.Warning,
    Message = "Tier-2 ephemeral pointer prune failed (non-fatal — retried next due interval)")]
  static partial void LogPointerPruneFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 24, Level = LogLevel.Debug,
    Message = "Stream-integrity ledger gauge refresh failed — convergence gauges will read stale until the next cycle")]
  static partial void LogLedgerGaugeRefreshFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 25, Level = LogLevel.Information,
    Message = "Digest-epoch closure sealed {EpochsClosed} epoch(s) — manifest answers for those ranges now read immutable folds")]
  static partial void LogEpochsClosed(ILogger logger, int epochsClosed);

  [LoggerMessage(EventId = 26, Level = LogLevel.Warning,
    Message = "Digest-epoch closure failed (non-fatal — the frontier advances on a later cycle)")]
  static partial void LogEpochClosureFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 30, Level = LogLevel.Warning,
    Message = "Table {Table} holds {Ratio}x the space its live rows need. Autovacuum cannot reclaim this; a rewrite can. Recorded — the post-ready Rewrite step performs it on the next boot when MaintenanceWorkerOptions.AllowTableRewrite permits (takes an ACCESS EXCLUSIVE lock), or rewrite it manually.")]
  static partial void LogRewriteNeeded(ILogger logger, string table, double ratio);

  [LoggerMessage(EventId = 33, Level = LogLevel.Warning, Message = "Recording the rewrite request for {Table} failed")]
  static partial void LogRewriteFailed(ILogger logger, string table, Exception ex);

  [LoggerMessage(EventId = 34, Level = LogLevel.Debug, Message = "Could not scan for tables needing rewrite")]
  static partial void LogRewriteScanFailed(ILogger logger, Exception ex);


  [LoggerMessage(EventId = 24, Level = LogLevel.Debug,
    Message = "PreDestruction hook ran for a batch of {TargetCount} ephemeral events (cancel={Cancel}, defer={Defer}) — decision not yet enforced (E2-2)")]
  static partial void LogPreDestruction(ILogger logger, int targetCount, bool cancel, bool defer);

  [LoggerMessage(EventId = 25, Level = LogLevel.Warning,
    Message = "PreDestruction hook threw for a batch of {TargetCount} ephemeral events — attempt {Attempt}/{MaxRetries}; {Outcome}")]
  static partial void LogPreDestructionFailed(ILogger logger, Exception ex, int targetCount, int attempt, int maxRetries, string outcome);

  [LoggerMessage(EventId = 26, Level = LogLevel.Warning,
    Message = "PostDestruction hook threw for a batch of {TargetCount} ephemeral events (non-fatal)")]
  static partial void LogPostDestructionFailed(ILogger logger, Exception ex, int targetCount);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information,
    Message = "Maintenance task '{TaskName}' affected {RowsAffected} rows in {DurationMs}ms")]
  static partial void LogMaintenanceResult(ILogger logger, string taskName, long rowsAffected, double durationMs);

  // v0.657 slice 5: stuck-row sentinel. One Warning per stuck row so operators
  // can grep by MessageId, GROUP BY MessageType to find spammy producers, and
  // correlate ClaimedSince against deploy boundaries.
  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "Stuck outbox row sentinel: message_id={MessageId} type={MessageType} stream={StreamId} attempts={Attempts} since={ClaimedSince:o} — row claimed past MaxOutboxAttempts but never drained. Investigate; see operations/observability/stuck-row-sentinel.")]
  static partial void LogStuckOutboxRow(ILogger logger, Guid messageId, string messageType, Guid? streamId, int attempts, DateTime claimedSince);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "Stuck inbox row sentinel: message_id={MessageId} type={MessageType} stream={StreamId} attempts={Attempts} since={ClaimedSince:o} — row claimed past MaxInboxAttempts but never drained. Investigate; see operations/observability/stuck-row-sentinel.")]
  static partial void LogStuckInboxRow(ILogger logger, Guid messageId, string messageType, Guid? streamId, int attempts, DateTime claimedSince);
}

/// <summary>Configuration for <see cref="MaintenanceWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class MaintenanceWorkerOptions {
  /// <summary>
  /// Killswitch. Set to <c>false</c> to disable the maintenance loop. Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Whether maintenance may rewrite a table to reclaim space it holds but cannot use.
  /// Default <c>false</c> — detect and report only.
  /// </summary>
  /// <remarks>
  /// A rewrite takes an ACCESS EXCLUSIVE lock for its duration, and the framework cannot know how
  /// large a consumer's table is. Taking that lock unattended is the operator's decision, so the
  /// default reports the need and does nothing. The backing SQL re-measures every candidate at
  /// call time, so enabling this cannot trigger a rewrite of a table that is already lean.
  /// </remarks>
  public bool AllowTableRewrite { get; set; }

  /// <summary>
  /// Interval between maintenance runs, in minutes. Default 10.
  /// <para>
  /// perform_maintenance is the only path that cleans abandoned <c>wh_active_streams</c> rows
  /// (rows whose owning instance is no longer in <c>wh_service_instances</c>). When an instance
  /// dies or is scaled in, its ownership rows persist until this loop runs. >10 min lets that
  /// accumulate, degrading owner-preferring claim. The audit gap #5 lock test in
  /// <c>MaintenanceWorkerTests.DefaultIntervalMinutes_IsLessThanOrEqualTo10MinutesAsync</c>
  /// fails if a future refactor weakens this default.
  /// </para>
  /// </summary>
  public int IntervalMinutes { get; set; } = 10;

  /// <summary>
  /// v0.657 slice 5: structural stuck-row sentinel killswitch. When <c>true</c>
  /// (default), the maintenance worker calls
  /// <see cref="IWorkCoordinator.FindStuckOutboxRowsAsync"/> and
  /// <see cref="IWorkCoordinator.FindStuckInboxRowsAsync"/> once per cycle and
  /// emits a Warning per row. Set to <c>false</c> if the canary ever becomes
  /// noisy and you want to disable it independently of the rest of maintenance.
  /// </summary>
  /// <docs>operations/observability/stuck-row-sentinel</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerStuckRowSentinelTests.cs:MaintenanceTick_SentinelDisabled_DoesNotInvokeSentinelMethodsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerStuckRowSentinelTests.cs:MaintenanceTick_NoStuckRows_EmitsNoSentinelWarningsAsync</tests>
  public bool StuckRowSentinelEnabled { get; set; } = true;

  /// <summary>
  /// Attempts threshold for the stuck-row sentinel. A row is "stuck" when
  /// <c>attempts &gt; StuckRowSentinelMaxAttempts</c> AND <c>processed_at IS NULL</c>.
  /// Default 10 — matches the
  /// <see cref="OutboxDrainWorkerOptions.MaxOutboxAttempts"/> default.
  /// </summary>
  /// <docs>operations/observability/stuck-row-sentinel</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerStuckRowSentinelTests.cs:MaintenanceTick_StuckOutboxRow_EmitsWarningPerRowAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerStuckRowSentinelTests.cs:MaintenanceTick_NoStuckRows_EmitsNoSentinelWarningsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerStuckRowSentinelTests.cs:MaintenanceTick_SentinelDisabled_DoesNotInvokeSentinelMethodsAsync</tests>
  public int StuckRowSentinelMaxAttempts { get; set; } = 10;

  /// <summary>
  /// Cap on returned stuck rows per cycle so Warning emission is bounded under
  /// saturation. Default 50 — high enough to catch a typical incident,
  /// low enough that log volume stays manageable.
  /// </summary>
  /// <docs>operations/observability/stuck-row-sentinel</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerStuckRowSentinelTests.cs:MaintenanceTick_StuckOutboxRow_EmitsWarningPerRowAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerStuckRowSentinelTests.cs:MaintenanceTick_MultipleStuckRows_OneWarningEachAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/MaintenanceWorkerStuckRowSentinelTests.cs:MaintenanceTick_SentinelDisabled_DoesNotInvokeSentinelMethodsAsync</tests>
  public int StuckRowSentinelLimit { get; set; } = 50;

  /// <summary>
  /// Backoff (seconds) before a FAILED destruction batch (a throwing <c>PreDestruction</c> hook) is retried.
  /// The batch is held for this long, so the next maintenance cycle re-offers it to the hook. Default 300 (5m).
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  public int DestructionRetryBackoffSeconds { get; set; } = 300;

  /// <summary>
  /// Max retries of a failing destruction batch before a FORCED delete (the reaper deletes the batch despite
  /// the failing hook, so a permanently-broken hook can never leak storage). Default 5.
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  public int MaxDestructionRetries { get; set; } = 5;

  /// <summary>
  /// The failure policy applied when a <c>PreDestruction</c> hook keeps failing: retry-then-forced-delete
  /// (default), retry-then-keep (never lose the data), or force-delete-immediately. The global default; a
  /// per-type <c>[Ephemeral(OnDestroyFailure)]</c> override is a later refinement.
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  public Lifecycle.OnDestroyFailure OnDestroyFailure { get; set; } = Lifecycle.OnDestroyFailure.RetryThenForcedDelete;
}
