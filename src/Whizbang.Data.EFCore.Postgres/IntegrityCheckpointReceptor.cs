// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Stream-integrity Phase B, consumer side — the built-in bridge that verifies a received
/// <see cref="IntegrityCheckpoint"/> against this service's own persisted receipts. Deficits found
/// in a window become PENDING; a deficit still present when the origin's NEXT checkpoint arrives
/// (two-cycle confirmation, absorbing in-flight stragglers) is CONFIRMED — a typed
/// <see cref="IntegrityGapDetected"/> report publishes, and at
/// <see cref="IntegrityRepairMode.AutoRepairCapped"/> a scoped
/// <see cref="RequestRedeliveryCommand"/> goes back to the origin WIRE-ONLY, directed via the R0
/// target (a lost request simply re-fires on the next confirmation cycle). Only types this
/// consumer keeps at the receive boundary (#2058: what it consumes, or a namespace it absorbs) are verified; a service's own checkpoints are ignored (locally
/// originated events carry no origin stamp to count against).
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCheckpointReceptorTests.cs</tests>
public sealed partial class IntegrityCheckpointReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrityCheckpointReceptor> logger) : IReceptor<IntegrityCheckpoint> {

  /// <inheritdoc />
  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "A checkpoint is actionable only when every collaborator is wired, it did not come from this service, and the store can measure counts. After that each pending bucket is recounted, confirmed or left alone, under a report cap and an optional auto-repair. The branches are the preconditions and the per-bucket verdicts.")]
  public async ValueTask HandleAsync(IntegrityCheckpoint message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await using var scope = scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var coordinator = services.GetService<IWorkCoordinator>();
    var tracker = services.GetService<IntegrityGapTracker>();
    var dispatcher = services.GetService<IDispatcher>();
    var options = services.GetService<IOptions<StreamIntegrityOptions>>()?.Value ?? new StreamIntegrityOptions();
    if (coordinator is null || tracker is null || dispatcher is null || !options.GapDetectionEnabled) {
      return;
    }

    // Announce this cycle so the cleanup sweep defers to it. Held for the whole handler: the two
    // walk the same tables, and overlapping them adds housekeeping-versus-housekeeping contention
    // on top of whatever live work already competes for those locks. A refused scope (another
    // cycle already running) is a no-op hold rather than a skip -- checkpoints arrive per origin
    // and dropping one would trade a lock collision for a missed gap.
    using var integrityHold =
      services.GetService<HousekeepingCoordinator>()?.BeginIntegrityScope()
      ?? default;

    var self = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
    if (message.OriginServiceId == self) {
      // Own checkpoint: locally-originated events persist NO origin stamp, so a self-count would
      // always read zero and every bucket would false-alarm.
      return;
    }

    // #2104: the kept state first, so a gap seen before a restart is confirmed by this checkpoint.
    await tracker.EnsureHydratedAsync(coordinator, cancellationToken).ConfigureAwait(false);
    var checkpointAt = DateTimeOffset.UtcNow;
    tracker.RecordCheckpoint(message.OriginServiceId, message.OriginServiceName, checkpointAt, message.RequestTopic);
    await coordinator.IntegrityRecordOriginCheckpointAsync(
      message.OriginServiceId, message.OriginServiceName, message.RequestTopic, checkpointAt, cancellationToken).ConfigureAwait(false);
    var pendingBefore = tracker.PeekPending(message.OriginServiceId);
    var metrics = services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
    metrics?.CheckpointsReceived.Add(1, new KeyValuePair<string, object?>("origin", message.OriginServiceName));
    // #2008: the origin's announced domains replace its claims; the map built from every claim decides which buckets
    // this origin is authoritative for (#2032).
    var ownership = await _recordOwnershipAsync(services, coordinator, metrics, message, cancellationToken).ConfigureAwait(false);
    // #2186: judged by the one rule the manifest comparison and the ledger sweep use, so a type in this service's own
    // owned domains is no other origin's to count, even when the map is silent about it.
    var ownDomains = services.GetService<IOptions<Whizbang.Core.Routing.RoutingOptions>>()?.Value.OwnedDomains
      ?? (IReadOnlyCollection<string>)[];
    var repairBudget = options.MaxAutoRepairRequestsPerCheckpoint;

    // MaxAutoRepairRequestsPerCheckpoint caps ONE checkpoint's batch. Checkpoints fire every
    // CheckpointIntervalSeconds, so on its own that bounds the storm's RATE and not its size: a
    // bucket whose events are genuinely gone never heals, is re-confirmed on every checkpoint, and
    // is re-requested for as long as the service runs. The repair ledger is what bounds the repeat,
    // through RepairRequestBackoffSeconds and MaxRepairAttemptsPerBucket, and the manifest path has
    // always consulted it. Same guard here, so both rungs are bounded in both dimensions.
    var ledger = IntegrityRepairSupport.ResolveLedger(services);
    var repairBackoff = TimeSpan.FromSeconds(options.RepairRequestBackoffSeconds);
    var repairNow = DateTimeOffset.UtcNow;

    // The full repair-decision policy for issue #582: three service-wide settledness signals
    // (depth, lag, live leases — any one vetoes, because each alone is fooled), a per-window
    // attempt budget that resets while the window is healing, and a global bound on how many
    // windows may be under repair at once. It existed, fully tested, with no caller; the storm it
    // was written to prevent happened anyway. The fallback keeps state per process so an unwired
    // host still gets bounded behavior rather than none.
    var repairPolicy = services.GetService<IntegrityRepairPolicy>() ?? _processFallbackPolicy;

    // Settledness is measured ONCE per checkpoint, service-wide. A consumer that is merely BEHIND
    // reports the same deficit on both confirmation cycles and confirms a gap while nothing has
    // been lost — the events are queued, not missing. Repairing then re-delivers work that is
    // already coming, which lengthens the queue that produced the false gap.
    //
    // null means the backend cannot answer. That is UNMEASURED, never settled: "nothing
    // outstanding" and "nobody looked" are the same value and opposite facts.
    var backlog = await coordinator.CountServiceBacklogAsync(cancellationToken).ConfigureAwait(false);

    // UNMEASURED falls back to the previous behavior rather than gating. A backend that has not
    // implemented the count would otherwise have self-healing silently switched off by a package
    // upgrade — the same silent-disable this codebase refuses elsewhere ("the 0s are precisely the
    // silent-disable this method exists to make impossible"). Stores that CAN report get the gate —
    // stores that cannot get a loud one-line warning and the old behavior.
    if (backlog is null) {
      LogSettlednessUnmeasurable(logger, message.OriginServiceName);
    }
    var gapReportCap = Math.Max(1, options.MaxGapReportsPerCheckpoint);
    var gapReportsPublished = 0;
    var confirmedGaps = 0;

    // 1) Two-cycle confirmation: deficits recorded on the PREVIOUS checkpoint, recounted now.
    foreach (var pending in tracker.TakePending(message.OriginServiceId)) {
      if (ownership.IsNotAuthoritative(pending.EventType, pending.OriginServiceId, ownDomains)) {
        continue;   // #2032, #2186: this origin does not own the type, so its count is not authoritative for it.
      }
      // Recount governor (#634). Repair is bounded per window; the recount that CONFIRMS the gap
      // was not, so an unhealable gap paid a full event-store scan on every checkpoint forever.
      // Inside the cooldown the gap stays known and reported from its last confirmation; only the
      // repeated scan is skipped, and the window re-registers from the current checkpoint as usual.
      if (!repairPolicy.ShouldRecount(
            pending.OriginServiceId, pending.EventType, pending.TenantScope,
            pending.FromCommitSequence, pending.ToCommitSequence, repairNow)) {
        continue;
      }
      var recount = await coordinator
        .CountReceivedFromOriginAsync(pending.OriginServiceId, pending.FromCommitSequence, pending.ToCommitSequence, cancellationToken)
        .ConfigureAwait(false);
      var actual = _bucketCount(recount, pending.TenantScope, pending.EventType);
      if (actual < pending.ExpectedCount) {
        actual += _heldCount(await _unsequencedAsync(coordinator, pending.OriginServiceId, [pending.EventType], cancellationToken)
          .ConfigureAwait(false), pending.TenantScope, pending.EventType);
      }
      var observation = new IntegrityRepairPolicy.GapObservation(
        pending.OriginServiceId, pending.EventType, pending.TenantScope,
        pending.FromCommitSequence, pending.ToCommitSequence,
        pending.ExpectedCount, actual,
        ServiceBacklogDepth: (int)Math.Min(backlog?.UnprocessedInboxRows ?? 0, int.MaxValue),
        ConsumerLag: backlog?.OldestUnprocessedAge ?? TimeSpan.Zero,
        ActiveLeaseCount: (int)Math.Min(backlog?.ActiveLeasedRows ?? 0, int.MaxValue));
      if (actual >= pending.ExpectedCount) {
        // Healed in flight — the straggler arrived between checkpoints. Releasing the window's
        // repair slot is what lets the global budget breathe: a slot held by a healed window would
        // starve a genuinely stuck one. Confirmation memory clears too, so a genuine
        // regression of the same window warns afresh.
        repairPolicy.RecordHealed(observation);
        tracker.ClearConfirmed(pending);
        // #2034: the type lane's ledger entry heals with its window, so a later gap of the type starts a fresh ladder
        // instead of inheriting spent attempts.
        await ledger.MarkHealedAsync(
          new IntegrityRepairLedger.DivergenceKey(pending.OriginServiceId, pending.TenantScope, pending.EventType, Guid.Empty),
          cancellationToken).ConfigureAwait(false);
        continue;
      }

      // #667: a deficit measured while the service is VISIBLY behind is expected
      // back-pressure, not loss — during a bulk ingest the producer runs ahead by design
      // and every in-flight window would read "expected N, have 0". CONFIRMED must mean
      // the pipeline is drained and the events are genuinely absent, so an unsettled
      // service defers: no confirmation, no warning, the pending carries to a later
      // cycle. UNMEASURED (backlog null) keeps the old behavior — same rationale as the
      // repair gate above: a store that cannot report must not have detection silently
      // switched off by an upgrade.
      //
      // Measured and not settled, in one test: the pattern only matches a backlog that was
      // measured, so the log below reads its counts without a second null check. IsQuiescent, not
      // IsSettled: since 167 the latter ignores the idle band, and a gap check that ran while idle
      // work was still queued could call a row missing that is merely not yet run. IsQuiescent is
      // the pre-167 meaning of IsSettled, so this gate is unchanged.
      if (backlog is { IsQuiescent: false }) {
        tracker.AddPending(pending);
        LogGapDeferredConsumerBehind(logger, pending.EventType, pending.TenantScope,
          pending.OriginServiceName, backlog.UnprocessedInboxRows, backlog.ActiveLeasedRows);
        continue;
      }

      if (repairPolicy.RecordRecount(observation, repairNow)) {
        LogRecountCooldownArmed(
          logger, pending.EventType, pending.TenantScope, pending.OriginServiceName, actual, pending.ExpectedCount);
      }

      // The policy decides from the SERVICE's settledness, never this instance's. Any of depth,
      // lag or live leases vetoes; a window that stopped healing exhausts its attempt budget; and
      // the global budget bounds how many windows may be under repair at once — the only cap that
      // limits the RATE at which repair adds load, since per-checkpoint caps reset every cadence.
      var autoRepair = options.RepairMode == IntegrityRepairMode.AutoRepairCapped
                    && repairBudget > 0
                    && repairPolicy.Evaluate(observation).ShouldRequestRepair;
      metrics?.GapsDetected.Add(1,
        new KeyValuePair<string, object?>("origin", pending.OriginServiceName),
        new KeyValuePair<string, object?>("event_type", pending.EventType));
      if (autoRepair) {
        // Burns an attempt against this bucket, returning false once it has spent
        // MaxRepairAttemptsPerBucket or is still inside its backoff window. Consulted only after the
        // cheaper gates: a grant records an attempt, so a grant we then discard would spend the
        // bucket's budget on a request that was never sent.
        autoRepair = await ledger.TryBeginRepairAsync(
          new IntegrityRepairLedger.DivergenceKey(
            pending.OriginServiceId, pending.TenantScope, pending.EventType, Guid.Empty),
          repairNow, repairBackoff, options.MaxRepairAttemptsPerBucket, cancellationToken)
          .ConfigureAwait(false);
      }
      if (autoRepair) {
        repairBudget--;
        metrics?.RepairsRequested.Add(1,
          new KeyValuePair<string, object?>("source", "checkpoint"),
          new KeyValuePair<string, object?>("origin", pending.OriginServiceName));
        await _sendRepairRequestAsync(services, ledger, tracker, options, pending, repairNow, cancellationToken).ConfigureAwait(false);
        // Charged only for a request that was actually sent, so the per-checkpoint budget and the
        // ledger cannot burn this window's attempt budget on requests that never left.
        repairPolicy.RecordRequested(observation);
      }
      // Each report is a durable outbox write and pendings are keyed by (tenant, event type), so
      // their number grows with the deployment rather than with any batch size. Past the cap we
      // count instead of publishing: an unhealed deficit is re-detected on the next checkpoint, so
      // the condition keeps surfacing until it is genuinely repaired.
      confirmedGaps++;

      // Log the confirmation before the publish gate (a capped report must not lose its log
      // line) — but WARN only on the window's FIRST confirmation (#667). An origin that
      // keeps checkpointing the same watermark re-registers the same deficit every cycle —
      // per-cycle repeats of an identical warning bury the log precisely when there is most
      // to read. Re-confirmations stay visible at Debug and countable on the meter.
      if (tracker.MarkConfirmed(pending)) {
        LogGapConfirmed(logger, pending.EventType, pending.TenantScope, pending.OriginServiceName,
          pending.FromCommitSequence, pending.ToCommitSequence, pending.ExpectedCount, actual, autoRepair);
      } else {
        LogGapReconfirmed(logger, pending.EventType, pending.TenantScope, pending.OriginServiceName,
          pending.FromCommitSequence, pending.ToCommitSequence, pending.ExpectedCount, actual);
      }

      // Opt-in: nothing consumes these and each mints its own stream. See
      // StreamIntegrityOptions.PublishReportEvents.
      if (!options.PublishReportEvents || gapReportsPublished >= gapReportCap) {
        continue;
      }
      gapReportsPublished++;
      await dispatcher.PublishAsync(new IntegrityGapDetected {
        ReportStreamId = TrackedGuid.New().Value,
        OriginServiceId = pending.OriginServiceId,
        OriginServiceName = pending.OriginServiceName,
        TenantScope = pending.TenantScope,
        EventType = pending.EventType,
        FromCommitSequence = pending.FromCommitSequence,
        ToCommitSequence = pending.ToCommitSequence,
        ExpectedCount = pending.ExpectedCount,
        ActualCount = actual,
        AutoRepairRequested = autoRepair,
      }).ConfigureAwait(false);
    }

    // Only a CAP warrants the warning — with publishing disabled (the production default) the
    // published count is always 0 and every confirmed gap would trigger it. Same rationale as
    // the manifest receptor's divergence-cap warning.
    if (options.PublishReportEvents && confirmedGaps > gapReportsPublished) {
      LogGapReportsCapped(logger, message.OriginServiceName, confirmedGaps, gapReportsPublished);
    }

    // 2) Evaluate THIS window: deficits become pendings the NEXT checkpoint confirms or clears.
    // #2058: only what the receive boundary keeps is this service's contract (what it consumes, or a namespace it
    // absorbs). A type it only knows is never stored here, so a count against it is a deficit that cannot heal.
    // #2032: and a type another service owns (or this service owns, #2186) is asked of its owner, not counted against an
    // older origin still counting it.
    var keeps = ConsumedMessageTypes.Keeps(services);
    var verified = message.Buckets
      .Where(b => keeps(b.EventType) && !ownership.IsNotAuthoritative(b.EventType, message.OriginServiceId, ownDomains))
      .ToList();
    if (verified.Count == 0) {
      await _keepPendingAsync(coordinator, tracker, message.OriginServiceId, pendingBefore, cancellationToken).ConfigureAwait(false);
      return;
    }
    var actualBuckets = await coordinator
      .CountReceivedFromOriginAsync(message.OriginServiceId, message.FromCommitSequence, message.ToCommitSequence, cancellationToken)
      .ConfigureAwait(false);
    // #2102: a window counts the events it can place by origin sequence. Events held from the origin without that
    // sequence may belong to it, so a short bucket counts them too; the read is paid only when a bucket reads short.
    var shortTypes = verified
      .Where(b => _bucketCount(actualBuckets, b.TenantScope, b.EventType) < b.Count)
      .Select(b => b.EventType).Distinct(StringComparer.Ordinal).ToList();
    var held = shortTypes.Count == 0
      ? []
      : await _unsequencedAsync(coordinator, message.OriginServiceId, shortTypes, cancellationToken).ConfigureAwait(false);
    foreach (var bucket in verified) {
      var actual = _bucketCount(actualBuckets, bucket.TenantScope, bucket.EventType)
        + _heldCount(held, bucket.TenantScope, bucket.EventType);
      if (actual < bucket.Count) {
        tracker.AddPending(new IntegrityGapTracker.PendingGap {
          OriginServiceId = message.OriginServiceId,
          OriginServiceName = message.OriginServiceName,
          TenantScope = bucket.TenantScope,
          EventType = bucket.EventType,
          FromCommitSequence = message.FromCommitSequence,
          ToCommitSequence = message.ToCommitSequence,
          ExpectedCount = bucket.Count,
        });
      }
    }
    await _keepPendingAsync(coordinator, tracker, message.OriginServiceId, pendingBefore, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// #2104: keeps the origin's pending gaps in the store when this checkpoint changed them (a gap confirmed, healed or
  /// first seen), so a restarted process confirms them with its first checkpoint. A checkpoint that changed nothing
  /// writes nothing.
  /// </summary>
  private static async Task _keepPendingAsync(
      IWorkCoordinator coordinator, IntegrityGapTracker tracker, Guid originServiceId,
      IReadOnlyList<IntegrityGapTracker.PendingGap> before, CancellationToken cancellationToken) {
    var after = tracker.PeekPending(originServiceId);
    if (after.SequenceEqual(before)) {
      return;
    }
    await coordinator.IntegrityReplacePendingGapsAsync(originServiceId, after, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Wire-only, directed at the origin — the pump's own publish pattern. A lost request
  /// re-fires on the next confirmation cycle, so no outbox durability is needed.</summary>
  // Per-process fallback for hosts that did not register the policy. Static so its window state
  // survives across checkpoints, which is the entire point; a per-call instance would evaluate
  // every checkpoint against an empty table and never exhaust anything.
  private static readonly IntegrityRepairPolicy _processFallbackPolicy = new(new IntegrityRepairPolicy.Settings());

  private async Task _sendRepairRequestAsync(
      IServiceProvider services, IIntegrityRepairLedger ledger, IntegrityGapTracker tracker, StreamIntegrityOptions options,
      IntegrityGapTracker.PendingGap pending, DateTimeOffset now, CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var requester = services.GetService<IServiceInstanceProvider>()?.ServiceName;
    var topic = IntegrityRepairSupport.ResolveRepairTopic(options, services);
    if (transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      LogRepairSkipped(logger, pending.OriginServiceName,
        transport is null, serializer is null, string.IsNullOrEmpty(requester), string.IsNullOrEmpty(topic));
      return;
    }

    var envelope = new MessageEnvelope<RequestRedeliveryCommand> {
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
      Payload = new RequestRedeliveryCommand {
        TenantScope = pending.TenantScope,
        EventTypes = [pending.EventType],
        FromCommitSequence = pending.FromCommitSequence,
        ToCommitSequence = pending.ToCommitSequence,
        RequesterService = requester,
        Topic = topic,
        AcceptsStreamSpanningBundles = true,
      },
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestRedeliveryCommand), services.GetService<IServiceInstanceProvider>(), DateTimeOffset.UtcNow)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = pending.OriginServiceName,
    };
    // Directed or not at all: without the origin-carried request address the ONLY other topic on
    // hand is the requester's own — publishing there fanned the request out to every service on
    // the shared topic (and back to the requester itself). The origin's next checkpoint carries
    // the address, and an unhealed deficit re-confirms then.
    // The tracker that held this pending gap, so it is there; it is what recorded the origin's address.
    var originRequestTopic = tracker.GetRequestTopic(pending.OriginServiceId);
    if (string.IsNullOrEmpty(originRequestTopic)) {
      LogRepairSkippedNoOriginTopic(logger, pending.OriginServiceName, pending.EventType);
      return;
    }
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.IntegrityControlDestination.For(originRequestTopic, envelope.MessageId.Value, typeof(RequestRedeliveryCommand), options), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
    // #2034: the whole-window request answers the type lane and the per-stream drain rows of the type inside it. The
    // ledger's window is the request's range shifted by one (an exclusive floor and an inclusive ceiling), as the drain
    // converts it the other way.
    _ = await ledger.MarkAnswerPendingAsync(
      [new IntegrityRepairLedger.DivergenceKey(pending.OriginServiceId, pending.TenantScope, pending.EventType, Guid.Empty)],
      pending.FromCommitSequence + 1, pending.ToCommitSequence + 1, now,
      TimeSpan.FromMinutes(options.RepairAnswerTimeoutMinutes), cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Records the origin's announced domains as its claim set, warns and counts each newly seen contested domain once
  /// per pair, and returns the map built from every claim on record.
  /// </summary>
  private async Task<IntegrityDomainOwnership> _recordOwnershipAsync(
      IServiceProvider services, IWorkCoordinator coordinator, Whizbang.Core.Observability.StreamIntegrityMetrics? metrics,
      IntegrityCheckpoint message, CancellationToken cancellationToken) {
    var conflicts = await coordinator.RecordIntegrityDomainClaimsAsync(
      message.OriginServiceId, message.OriginServiceName, message.RequestTopic, message.OwnedDomains, cancellationToken)
      .ConfigureAwait(false);
    var seen = services.GetService<IntegrityOwnershipConflicts>() ?? _processConflicts;
    foreach (var conflict in conflicts.Where(c => seen.FirstSighting(c.Domain, message.OriginServiceId, c.OwnerServiceId))) {
      metrics?.OwnershipConflicts.Add(1, new KeyValuePair<string, object?>("domain", conflict.Domain));
      LogOwnershipConflict(logger, conflict.Domain, message.OriginServiceName, message.OriginServiceId,
        conflict.OwnerServiceName, conflict.OwnerServiceId);
    }
    return new IntegrityDomainOwnership(
      await coordinator.GetIntegrityDomainClaimsAsync(cancellationToken).ConfigureAwait(false));
  }

  // Per-process warn-once memory for hosts that did not register one; static so it survives across checkpoints.
  private static readonly IntegrityOwnershipConflicts _processConflicts = new();

  private static int _bucketCount(IReadOnlyList<CheckpointBucket> buckets, string? tenantScope, string eventType) =>
    buckets.Where(b => b.EventType == eventType && b.TenantScope == tenantScope).Sum(b => b.Count);

  /// <summary>#2102: the (tenant, type) folds of the events held from the origin without its commit sequence. No
  /// settle horizon: the checkpoint's own count has none.</summary>
  private static Task<IReadOnlyList<StreamDigest>> _unsequencedAsync(
      IWorkCoordinator coordinator, Guid originServiceId, IReadOnlyList<string> eventTypes, CancellationToken cancellationToken) =>
    coordinator.ComputeUnsequencedReceivedDigestsAsync(originServiceId, eventTypes, streamIds: null, TimeSpan.Zero, cancellationToken);

  private static int _heldCount(IReadOnlyList<StreamDigest> held, string? tenantScope, string eventType) =>
    held.Where(d => d.EventType == eventType && d.TenantScope == tenantScope).Sum(d => d.EventCount);

  [LoggerMessage(EventId = 58, Level = LogLevel.Warning,
    Message = "CONFIRMED integrity gap reports capped for origin '{OriginServiceName}': {ConfirmedGaps} confirmed, " +
              "{Reported} reported. The remainder is re-detected on the next checkpoint.")]
  static partial void LogGapReportsCapped(ILogger logger, string originServiceName, int confirmedGaps, int reported);

  [LoggerMessage(EventId = 50, Level = LogLevel.Warning,
    Message = "CONFIRMED integrity gap: {EventType} (tenant {TenantScope}) from origin '{OriginServiceName}' " +
              "window ({FromCommitSequence}, {ToCommitSequence}] — expected {ExpectedCount}, have {ActualCount} " +
              "(autoRepair={AutoRepairRequested})")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "LoggerMessage source-generated method: the parameter list mirrors the structured log template's placeholders and cannot be grouped without losing structured-logging semantics.")]
  static partial void LogGapConfirmed(ILogger logger, string eventType, string? tenantScope, string originServiceName,
    long fromCommitSequence, long toCommitSequence, int expectedCount, int actualCount, bool autoRepairRequested);

  [LoggerMessage(EventId = 60, Level = LogLevel.Warning,
    Message = "Cannot measure whether this service has settled (the store does not implement "
            + "CountServiceBacklogAsync), so auto-repair for origin '{OriginServiceName}' proceeds "
            + "UNGATED. A consumer that is merely behind may confirm false gaps and re-deliver work "
            + "that is already queued. Implement the count, or set RepairMode=ReportOnly.")]
  static partial void LogSettlednessUnmeasurable(ILogger logger, string originServiceName);

  // EventId 59 (auto-repair withheld while the consumer is behind) is retired: the deferral guard
  // returns before a confirmation, so that line could never fire (issue #708). Its content, that a
  // service deliberately withholding repair while it drains is not one with repair disabled, is
  // carried by the deferral line (62) instead. Do not reuse the id.

  [LoggerMessage(EventId = 62, Level = LogLevel.Debug,
    Message = "Integrity deficit for {EventType} (tenant {TenantScope}) from origin '{OriginServiceName}' deferred: "
            + "consumer visibly behind (unprocessed={UnprocessedRows}, leased={LeasedRows}) — in-flight lag, not loss; "
            + "repair is withheld until this service settles (repairing now would re-deliver work already in flight); "
            + "re-evaluated when settled. A value of -1 means the backend could not report, which is treated as NOT settled")]
  static partial void LogGapDeferredConsumerBehind(ILogger logger, string eventType, string? tenantScope,
    string originServiceName, long unprocessedRows, long leasedRows);

  [LoggerMessage(EventId = 63, Level = LogLevel.Debug,
    Message = "Integrity gap re-confirmed (already warned): {EventType} (tenant {TenantScope}) from origin '{OriginServiceName}' "
            + "window ({FromCommitSequence}, {ToCommitSequence}] — expected {ExpectedCount}, have {ActualCount}")]
#pragma warning disable S107 // the log line names every fact of the gap; a parameter object would hide them from the message template
  static partial void LogGapReconfirmed(ILogger logger, string eventType, string? tenantScope,
    string originServiceName, long fromCommitSequence, long toCommitSequence, int expectedCount, int actualCount);
#pragma warning restore S107

  [LoggerMessage(EventId = 51, Level = LogLevel.Warning,
    Message = "Auto-repair request to '{OriginServiceName}' skipped — missing infrastructure " +
              "(transport={TransportMissing}, serializer={SerializerMissing}, requester={RequesterMissing}, topic={TopicMissing})")]
  static partial void LogRepairSkipped(ILogger logger, string originServiceName,
    bool transportMissing, bool serializerMissing, bool requesterMissing, bool topicMissing);

  [LoggerMessage(EventId = 64, Level = LogLevel.Information,
    Message = "Repair request to '{OriginServiceName}' withheld ({EventType}) — no origin-carried " +
              "request address yet; the origin's next checkpoint teaches it")]
  static partial void LogRepairSkippedNoOriginTopic(ILogger logger, string originServiceName, string eventType);

  [LoggerMessage(EventId = 65, Level = LogLevel.Warning,
    Message = "Domain '{Domain}' is claimed by two services, '{OriginServiceName}' ({OriginServiceId}) and " +
              "'{RivalServiceName}' ({RivalServiceId}); neither is used as its owner until one gives it up. Check both " +
              "services' owned-domain registrations")]
  static partial void LogOwnershipConflict(ILogger logger, string domain, string originServiceName, Guid originServiceId,
    string rivalServiceName, Guid rivalServiceId);

  [LoggerMessage(EventId = 61, Level = LogLevel.Warning,
    Message = "Recount cooldown armed for {EventType} (tenant {TenantScope}) from origin {OriginServiceName}: the count has not moved ({Actual}/{Expected}) across the configured threshold. The gap stays reported; one recount runs per cooldown period, and any improvement lifts it immediately")]
  static partial void LogRecountCooldownArmed(
    ILogger logger, string eventType, string? tenantScope, string originServiceName, int actual, int expected);
}
