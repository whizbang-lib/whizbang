using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Stream-integrity Phase A, ORIGIN side — answers a received
/// <see cref="RequestIntegrityManifest"/> with this service's identity digests, published
/// wire-only as <see cref="IntegrityManifest"/> chunks TARGETED back at the requester. A1c: the
/// answer honors the requested <see cref="ManifestLevel"/> and source — table-driven by default
/// (<see cref="IWorkCoordinator.GetStreamDigestsAsync"/> /
/// <see cref="IWorkCoordinator.GetTypeDigestsAsync"/>, O(buckets)); a sweep request
/// (<see cref="RequestIntegrityManifest.UseRecompute"/>) or a provider without the digest table
/// falls back to the full recompute. An origin with no matching history stays silent —
/// comparison is per-bucket, so absence means nothing to audit, and a lost chunk's buckets
/// simply re-audit next cycle.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs</tests>
public sealed partial class IntegrityManifestRequestReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrityManifestRequestReceptor> logger) : IReceptor<RequestIntegrityManifest> {

  /// <summary>
  /// One manifest answer at a time per process. Requests arrive in bursts (every consumer audits
  /// on a similar cadence after a deploy), and each answer may recompute digests over the whole
  /// store; unbounded concurrency multiplies that footprint. Requests queue here — inbox
  /// redelivery semantics make waiting safe.
  /// </summary>
  private static readonly SemaphoreSlim _answerGate = new(1, 1);

  /// <inheritdoc />
  public async ValueTask HandleAsync(RequestIntegrityManifest message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await _answerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      await _handleCoreAsync(message, cancellationToken).ConfigureAwait(false);
    } finally {
      _answerGate.Release();
    }
  }

  private async Task _handleCoreAsync(RequestIntegrityManifest message, CancellationToken cancellationToken) {
    var answerTimer = System.Diagnostics.Stopwatch.StartNew();
    await using var scope = scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var coordinator = services.GetService<IWorkCoordinator>();
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var options = services.GetService<IOptions<StreamIntegrityOptions>>()?.Value ?? new StreamIntegrityOptions();
    if (coordinator is null || transport is null || serializer is null) {
      LogMissingInfrastructure(logger, coordinator is null, transport is null, serializer is null);
      return;
    }

    var settle = TimeSpan.FromMinutes(options.AuditSettleWindowMinutes);
    IReadOnlyList<StreamDigest> digests;
    var recomputed = message.UseRecompute;
    long? computedThrough = null;
    Guid? resumeAfter = null;
    var answeredWindowed = false;

    if (message.Windowed) {
      // #80-B negotiated scope: answer only [SinceSequence, UntilSequence) — epoch-served, so the
      // cost is the open window, not the store. A null result means the engine cannot window;
      // the honest fallback is the legacy full answer below (correct, just unbounded) — never a
      // fabricated watermark the engine cannot stand behind.
      var windowed = message.Level == ManifestLevel.Types
        ? await coordinator.ComputeTypeDigestsWindowedAsync(
            null, message.EventTypes, message.SinceSequence, message.UntilSequence, settle,
            cancellationToken).ConfigureAwait(false)
        : await coordinator.ComputeStreamDigestsWindowedAsync(
            null, message.EventTypes, message.SinceSequence, message.UntilSequence,
            message.ResumeAfterStreamId, message.MaxDigests ?? options.MaxDigestsPerManifest, settle,
            cancellationToken).ConfigureAwait(false);
      if (windowed is not null) {
        if (windowed.Digests.Count == 0 && windowed.ComputedThrough <= message.SinceSequence) {
          return;   // nothing settled beyond the asker's watermark — no progress to report.
        }
        digests = windowed.Digests;
        computedThrough = windowed.ComputedThrough;
        resumeAfter = windowed.ResumeAfterStreamId;
        recomputed = true;   // windowed answers are compute-class by construction
        answeredWindowed = true;
      } else {
        digests = [];
      }
    } else {
      digests = [];
    }

    if (!answeredWindowed) {
      if (!message.UseRecompute) {
        digests = message.Level == ManifestLevel.Types
          ? await coordinator.GetTypeDigestsAsync(null, message.EventTypes, cancellationToken).ConfigureAwait(false)
          : await coordinator.GetStreamDigestsAsync(null, message.EventTypes, cancellationToken).ConfigureAwait(false);
        // Empty table read: either genuinely nothing emitted OR the provider has no digest table
        // (the DIM default). The recompute distinguishes them — cheap when truly empty.
        recomputed = digests.Count == 0;
      }
      if (recomputed) {
        // Types-level answers roll up AT THE STORE — materializing one row per stream to answer a
        // types-level request has memory-killed origins with large stores.
        digests = message.Level == ManifestLevel.Types
          ? await coordinator.ComputeTypeDigestsAsync(null, message.EventTypes, settle, cancellationToken).ConfigureAwait(false)
          : await coordinator.ComputeStreamDigestsAsync(null, message.EventTypes, settle, cancellationToken).ConfigureAwait(false);
      }
      if (digests.Count == 0) {
        return;   // nothing this origin emitted for those types — silence, not an empty manifest.
      }
    }

    var originServiceId = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
    // #80-F: every answer carries the history generation — how a consumer distinguishes a
    // deliberate mutation of folded history (reset + re-verify) from damage (alarm).
    var originGeneration = await coordinator.GetIntegrityOriginGenerationAsync(cancellationToken).ConfigureAwait(false);
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var originName = instanceProvider?.ServiceName ?? string.Empty;
    // All chunks of one manifest share the manifest stream (the origin id) as their session —
    // session-enabled subscriptions dead-letter sessionless deliveries, and a shared session
    // keeps chunk order.
    var destination = Whizbang.Core.Transports.ControlPlaneDestination.For(message.Topic, originServiceId, typeof(IntegrityManifest));
    var chunks = 0;

    // do-while: a WINDOWED quiet window (zero digests, watermark advanced) must still publish —
    // only an answer can carry the watermark, and silence would freeze the asker's seal forever
    // on a quiet type. The legacy path never reaches here empty.
    var totalChunks = Math.Max(1, (digests.Count + options.MaxDigestsPerManifest - 1) / options.MaxDigestsPerManifest);
    var offset = 0;
    do {
      var chunk = digests.Skip(offset).Take(options.MaxDigestsPerManifest).ToList();
      var envelope = new MessageEnvelope<IntegrityManifest> {
        MessageId = new MessageId(TrackedGuid.NewMedo()),
        Payload = new IntegrityManifest {
          ManifestStreamId = originServiceId,
          OriginServiceId = originServiceId,
          OriginServiceName = originName,
          Digests = chunk,
          Level = message.Level,
          Recomputed = recomputed,
          SinceSequence = answeredWindowed ? message.SinceSequence : null,
          ComputedThrough = computedThrough,
          ResumeAfterStreamId = resumeAfter,
          ChunkCount = totalChunks,
          OriginGeneration = originGeneration,
        },
        Hops = [
          Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(IntegrityManifest), instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown, DateTimeOffset.UtcNow)
        ],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
        Target = message.RequesterService,
      };
      var serialized = serializer.SerializeEnvelope(envelope);
      await transport.PublishAsync(serialized.JsonEnvelope, destination, serialized.EnvelopeType,
        cancellationToken: cancellationToken).ConfigureAwait(false);
      chunks++;
      offset += options.MaxDigestsPerManifest;
    } while (offset < digests.Count);
    var answerMetrics = services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
    answerMetrics?.ManifestChunksSent.Add(chunks,
      new KeyValuePair<string, object?>("level", message.Level.ToString()));
    answerMetrics?.ManifestAnswerDuration.Record(answerTimer.Elapsed.TotalSeconds,
      new KeyValuePair<string, object?>("level", message.Level.ToString()),
      new KeyValuePair<string, object?>("windowed", answeredWindowed),
      new KeyValuePair<string, object?>("recompute", recomputed));
    LogManifestSent(logger, digests.Count, chunks, message.RequesterService);
  }

  [LoggerMessage(EventId = 52, Level = LogLevel.Warning,
    Message = "RequestIntegrityManifest received but required infrastructure is missing " +
              "(coordinator={CoordinatorMissing}, transport={TransportMissing}, serializer={SerializerMissing}); ignored")]
  static partial void LogMissingInfrastructure(ILogger logger, bool coordinatorMissing, bool transportMissing, bool serializerMissing);

  [LoggerMessage(EventId = 53, Level = LogLevel.Information,
    Message = "Integrity manifest sent: {DigestCount} digest(s) in {ChunkCount} chunk(s) to '{RequesterService}'")]
  static partial void LogManifestSent(ILogger logger, int digestCount, int chunkCount, string requesterService);
}

/// <summary>
/// Stream-integrity Phase A, CONSUMER side — compares a received <see cref="IntegrityManifest"/>
/// chunk against this service's own from-that-origin digests. A bucket that is missing locally or
/// whose fold differs raises <see cref="IntegrityDivergenceDetected"/>; at
/// <see cref="IntegrityRepairMode.AutoRepairCapped"/> a stream-scoped
/// <see cref="RequestRedeliveryCommand"/> (repair semantics — receptors and all) goes back to the
/// origin, capped per chunk. Local EXTRAS are reported by the taxonomy as investigation items and
/// never auto-deleted; extra detection needs the full manifest set and rides a later increment.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs</tests>
public sealed partial class IntegrityManifestReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrityManifestReceptor> logger) : IReceptor<IntegrityManifest> {

  /// <summary>
  /// One manifest comparison at a time per process. Manifest chunks arrive in bursts (every
  /// origin answers a fresh audit at once), and a comparison against an unpopulated digest lane
  /// falls back to a full-store recompute; unbounded concurrency multiplies that footprint —
  /// observed live as consumers memory-cycling through their first full audit wave.
  /// </summary>
  private static readonly SemaphoreSlim _compareGate = new(1, 1);

  /// <inheritdoc />
  public async ValueTask HandleAsync(IntegrityManifest message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    if (message.Digests.Count == 0) {
      return;
    }
    // NON-QUEUEING by design. Waiting here looked free but was not: every queued manifest held
    // its fully-deserialized digest payload while it waited, and when manifests arrive faster
    // than one comparison completes, that queue IS the heap — observed live as a fleet-wide
    // OOM-crashloop whose restart storm then amplified the very arrival rate it could not serve.
    // A skipped chunk is the DOCUMENTED benign case (comparison is per-bucket independent; its
    // buckets simply re-audit next cycle), so the memory-safe move is to decline, not to wait.
    if (!await _compareGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false)) {
      LogCompareBusySkipped(logger, message.OriginServiceName, message.Digests.Count);
      _declinedMetrics()?.ComparesDeclined.Add(1,
        new KeyValuePair<string, object?>("origin", message.OriginServiceName));
      return;
    }
    var compareTimer = System.Diagnostics.Stopwatch.StartNew();
    try {
      await _handleCoreAsync(message, cancellationToken).ConfigureAwait(false);
    } finally {
      _compareGate.Release();
      // Recorded gate-to-done: when p99 approaches the manifest arrival interval, chunks are
      // being declined at the gate — the compare-slower-than-arrival pressure reading.
      _declinedMetrics()?.ManifestCompareDuration.Record(compareTimer.Elapsed.TotalSeconds,
        new KeyValuePair<string, object?>("level", message.Level.ToString()));
    }
  }

  /// <summary>Metrics for the gate path, which runs before any scope exists. Root-resolved and
  /// cached — StreamIntegrityMetrics is a singleton wherever it is registered at all.</summary>
  private Whizbang.Core.Observability.StreamIntegrityMetrics? _gateMetrics;
  private bool _gateMetricsResolved;

  private Whizbang.Core.Observability.StreamIntegrityMetrics? _declinedMetrics() {
    if (!_gateMetricsResolved) {
      using var scope = scopeFactory.CreateScope();
      _gateMetrics = scope.ServiceProvider.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
      _gateMetricsResolved = true;
    }
    return _gateMetrics;
  }

  private async Task _handleCoreAsync(IntegrityManifest message, CancellationToken cancellationToken) {
    await using var scope = scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var coordinator = services.GetService<IWorkCoordinator>();
    var dispatcher = services.GetService<IDispatcher>();
    var options = services.GetService<IOptions<StreamIntegrityOptions>>()?.Value ?? new StreamIntegrityOptions();
    if (coordinator is null || dispatcher is null || !options.AuditEnabled) {
      return;
    }

    var self = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
    if (message.OriginServiceId == self) {
      return;   // own manifest looping back — nothing to compare against ourselves.
    }

    // #80-F: the generation guard runs before ANY comparison. A changed generation means the
    // origin's history legitimately moved (a close, a reclassification) — this round's windows
    // and folds were aligned to the OLD world, and comparing them would alarm on deliberate
    // change. The guard resets the seal once; the next audit re-verifies from the beginning.
    if (message.OriginGeneration is long carriedGeneration) {
      var coherent = await coordinator.EnsureIntegritySealGenerationAsync(
        message.OriginServiceId, carriedGeneration, cancellationToken).ConfigureAwait(false);
      if (!coherent) {
        LogGenerationChanged(logger, message.OriginServiceName, carriedGeneration);
        return;
      }
    }

    var types = message.Digests.Select(d => d.EventType).Distinct(StringComparer.Ordinal).ToList();
    var settle = TimeSpan.FromMinutes(options.AuditSettleWindowMinutes);

    if (message.Level == ManifestLevel.Types) {
      await _handleTypeLevelAsync(services, options, message, types, settle, cancellationToken).ConfigureAwait(false);
      return;
    }

    // Stream level. The local fold is bounded by the CHUNK: a lane can hold a million streams,
    // the chunk names at most the origin's chunk size, and the comparison only needs those.
    // Observed live (fleet-wide OOM-crashloop): the windowed compare folded the WHOLE window and
    // the legacy fallback recomputed the WHOLE store to check one 500-stream chunk. A windowed
    // manifest (#80-B) still compares window-vs-window — via the chunk-bounded fold's sequence
    // range — because window counts against all-history folds would fabricate mismatches on any
    // stream with prior history.
    var chunkStreams = message.Digests.Select(d => d.StreamId).Distinct().ToList();
    IReadOnlyList<StreamDigest> local;
    if (message.ComputedThrough is long streamWindowEnd) {
      var bounded = await coordinator.ComputeStreamDigestsForChunkAsync(
        message.OriginServiceId, chunkStreams, message.SinceSequence ?? 0, streamWindowEnd, settle,
        cancellationToken).ConfigureAwait(false);
      if (bounded is null) {
        // This engine cannot fold the manifest's window; comparing incomparable ranges would
        // alarm on phantoms. Skip — the origin re-answers next cycle, legacy if we keep failing.
        LogWindowedCompareUnsupported(logger, message.OriginServiceName);
        return;
      }
      local = bounded;
    } else if (message.Recomputed) {
      // Sweep manifests compare recompute-to-recompute — now bounded to the chunk's streams.
      // The whole-store recompute survives ONLY as the compat path for engines without the
      // bounded fold; it is the memory-killer on large lanes.
      local = await coordinator.ComputeStreamDigestsForChunkAsync(
          message.OriginServiceId, chunkStreams, sinceSequence: null, untilSequence: null, settle,
          cancellationToken).ConfigureAwait(false)
        ?? await coordinator.ComputeStreamDigestsAsync(message.OriginServiceId, types, settle, cancellationToken).ConfigureAwait(false);
    } else {
      // Table manifests keep table-to-table semantics (settle-skip rides UpdatedAt). Only the
      // UNPOPULATED-table fallback changes: it used to be the whole-store recompute — the other
      // shape of the same OOM — and is now the chunk-bounded fold over full history.
      var table = await coordinator.GetStreamDigestsAsync(message.OriginServiceId, types, cancellationToken).ConfigureAwait(false);
      local = table.Count > 0
        ? table
        : await coordinator.ComputeStreamDigestsForChunkAsync(
            message.OriginServiceId, chunkStreams, sinceSequence: null, untilSequence: null, settle,
            cancellationToken).ConfigureAwait(false)
          ?? await coordinator.ComputeStreamDigestsAsync(message.OriginServiceId, types, settle, cancellationToken).ConfigureAwait(false);
    }
    var localByBucket = local.ToDictionary(d => (d.TenantScope, d.EventType, d.StreamId));

    // Convergence bounding: the ledger suppresses re-reports of unchanged divergence inside the
    // cooldown and backs off repair re-requests per bucket — a persistent divergence (origin
    // down, damaged bucket) must trickle, not storm. Unregistered ledger (bare tests) degrades
    // to per-call state: everything reports once per manifest, still batched and still directed.
    // Prefer a durable ledger. The in-memory one is per-process and dies on restart, which is
    // sound only while restarts are rare — and here the report storm is what CAUSES the restarts,
    // so every boot cleared the state that would have suppressed it. It is also per-replica, so
    // each pod reported the same divergence independently.
    var ledger = services.GetService<IIntegrityRepairLedger>()
      ?? (IIntegrityRepairLedger?)services.GetService<IntegrityRepairLedger>()
      ?? new IntegrityRepairLedger();
    var cooldown = TimeSpan.FromMinutes(options.DivergenceReportCooldownMinutes);
    var backoff = TimeSpan.FromSeconds(options.RepairRequestBackoffSeconds);
    var now = DateTimeOffset.UtcNow;
    var metrics = services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
    var repairBudget = options.MaxAutoRepairRequestsPerAudit;
    var repairBatches = new Dictionary<(string? TenantScope, string EventType), List<Guid>>();
    var divergenceTallies = new Dictionary<(string? TenantScope, string EventType), _divergenceTally>();
    var reportCap = Math.Max(1, options.MaxDivergenceReportsPerManifest);
    var reportsPublished = 0;
    var divergentSeen = 0;

    // PASS 1 — pure memory: classify every bucket. The ledger is then consulted in THREE batch
    // round trips (healed / report / repair) instead of up to two PER BUCKET. The per-bucket
    // consult made each chunk's comparison seconds long — slower than manifests arrive — and
    // every waiting manifest held its deserialized payload behind the compare gate until the
    // process died (observed live, fleet-wide).
    var healedKeys = new List<IntegrityRepairLedger.DivergenceKey>();
    var divergent = new List<(StreamDigest Origin, StreamDigest? Mine, bool IsDeficit, string Reason)>();
    foreach (var origin in message.Digests) {
      localByBucket.TryGetValue((origin.TenantScope, origin.EventType, origin.StreamId), out var mine);
      if (mine is not null && mine.DigestLo == origin.DigestLo && mine.DigestHi == origin.DigestHi) {
        // provably complete — a later divergence is a fresh incident.
        healedKeys.Add(new IntegrityRepairLedger.DivergenceKey(
          message.OriginServiceId, origin.TenantScope, origin.EventType, origin.StreamId));
        continue;
      }
      if (!message.Recomputed && IntegrityDigestMath.IsInsideSettle(origin.UpdatedAt, mine?.UpdatedAt, settle)) {
        continue;   // the bucket changed inside the settle window — in-flight, not divergence.
      }

      // #80-C taxonomy — only a DEFICIT is repairable. Equal counts with differing folds mean the
      // consumer holds the same NUMBER of events with different IDENTITY: redelivery re-ships what
      // it already has, dedup drops it, the fold never moves — a repair loop that can never
      // converge (observed live as an unbounded redelivery storm). A local SURPLUS cannot be fixed
      // by asking the origin for more, and local history is never auto-deleted on a remote's
      // say-so. Both alarm; neither repairs.
      var localCount = mine?.EventCount ?? 0;
      var isDeficit = localCount < origin.EventCount;
      var reason = isDeficit ? "deficit" : localCount == origin.EventCount ? "identity_mismatch" : "local_extra";
      divergent.Add((origin, mine, isDeficit, reason));
    }

    if (healedKeys.Count > 0) {
      // The heal's own delete reports each bucket's first-sighting age — per-stream
      // time-to-reconcile, measured by destroying the row that carried the clock.
      var healAges = await ledger.MarkHealedBatchWithAgesAsync(healedKeys, cancellationToken).ConfigureAwait(false);
      foreach (var age in healAges) {
        metrics?.BucketHealSeconds.Record(age);
      }
    }
    var observations = new IntegrityReportObservation[divergent.Count];
    for (var i = 0; i < divergent.Count; i++) {
      var (origin, mine, _, _) = divergent[i];
      observations[i] = new IntegrityReportObservation(
        new IntegrityRepairLedger.DivergenceKey(message.OriginServiceId, origin.TenantScope, origin.EventType, origin.StreamId),
        origin.DigestLo, origin.DigestHi, mine?.DigestLo ?? 0, mine?.DigestHi ?? 0);
    }
    var reportFlags = divergent.Count > 0
      ? await ledger.TryBeginReportBatchAsync(observations, now, cooldown, cancellationToken).ConfigureAwait(false)
      : [];
    // Repair eligibility batches only the DEFICITS, in manifest order, with the budget enforced
    // INSIDE the batch — past the cap keys are not consulted, so no attempt budget is burned on
    // grants that would be discarded.
    var deficitIndexes = new List<int>();
    for (var i = 0; i < divergent.Count; i++) {
      if (divergent[i].IsDeficit) {
        deficitIndexes.Add(i);
      }
    }
    // Paced-drain mode (the default): the compare is DISCOVERY-ONLY — deficits are recorded
    // (reports above) and their compared window stamped for the drain worker, which dispatches
    // continuously at an adaptive rate instead of bursting the budget at the audit tick. The
    // legacy burst path remains behind RepairDrainEnabled=false for engines without a
    // drain-capable coordinator.
    if (options.RepairDrainEnabled && deficitIndexes.Count > 0 && message.ComputedThrough is long stampUntil) {
      var stampKeys = deficitIndexes.Select(i => observations[i].Key).ToList();
      var stampCoordinator = services.GetService<IWorkCoordinator>();
      if (stampCoordinator is not null) {
        await stampCoordinator.IntegrityStampRepairWindowsAsync(
          message.OriginServiceId, stampKeys, message.SinceSequence ?? 0, stampUntil, cancellationToken).ConfigureAwait(false);
      }
    }
    var repairEligible = options.RepairMode == IntegrityRepairMode.AutoRepairCapped && repairBudget > 0
      && !options.RepairDrainEnabled;
    IReadOnlyList<bool> repairFlags = [];
    if (repairEligible && deficitIndexes.Count > 0) {
      var deficitKeys = deficitIndexes.Select(i => observations[i].Key).ToList();
      repairFlags = await ledger.TryBeginRepairBatchAsync(
        deficitKeys, now, backoff, options.MaxRepairAttemptsPerBucket, repairBudget, cancellationToken).ConfigureAwait(false);
    }
    var deficitFlagByIndex = new Dictionary<int, bool>();
    for (var d = 0; d < deficitIndexes.Count; d++) {
      deficitFlagByIndex[deficitIndexes[d]] = d < repairFlags.Count && repairFlags[d];
    }

    // PASS 2 — apply the batched decisions: metrics, repair batching, tallies, capped publishes.
    for (var i = 0; i < divergent.Count; i++) {
      var (origin, mine, _, reason) = divergent[i];
      metrics?.DivergencesDetected.Add(1,
        new KeyValuePair<string, object?>("origin", message.OriginServiceName),
        new KeyValuePair<string, object?>("event_type", origin.EventType),
        new KeyValuePair<string, object?>("reason", reason));
      var shouldReport = i < reportFlags.Count && reportFlags[i];
      var autoRepair = deficitFlagByIndex.TryGetValue(i, out var granted) && granted;
      if (autoRepair) {
        metrics?.RepairsRequested.Add(1,
          new KeyValuePair<string, object?>("source", "audit"),
          new KeyValuePair<string, object?>("origin", message.OriginServiceName));
        if (!repairBatches.TryGetValue((origin.TenantScope, origin.EventType), out var streams)) {
          repairBatches[(origin.TenantScope, origin.EventType)] = streams = [];
        }
        streams.Add(origin.StreamId);
      }
      if (!shouldReport) {
        continue;   // same unhealed divergence inside the cooldown — cadence, not news.
      }

      // Each report is a durable outbox write. Past the cap we stop publishing and only count:
      // a manifest carries up to MaxDigestsPerManifest buckets, and issuing that many sequential
      // writes inside one handler starved the HTTP pipeline until liveness failed and the pod was
      // killed. Nothing is lost — the ledger keeps these unhealed, so the next comparison
      // re-offers whatever is still divergent and a real problem still converges.
      divergentSeen++;

      // Aggregate rather than log per stream. One line per (tenant, type) carrying a count and a
      // sample is what an operator can actually act on; hundreds of near-identical lines bury the
      // signal and cost real work on the same thread that owes the liveness probe an answer.
      //
      // This runs for EVERY divergent bucket, before the publish gate. It used to sit after it,
      // which meant a capped report also lost its line in the tally — so the aggregate under-counted
      // exactly when there was most to report, and switching publishing off would have silenced the
      // operator-facing log entirely rather than only the durable writes.
      if (!divergenceTallies.TryGetValue((origin.TenantScope, origin.EventType), out var tally)) {
        tally = new _divergenceTally { SampleStreamId = origin.StreamId };
        divergenceTallies[(origin.TenantScope, origin.EventType)] = tally;
      }
      tally.Count++;
      tally.OriginTotal += origin.EventCount;
      tally.LocalTotal += mine?.EventCount ?? 0;
      tally.AnyAutoRepair |= autoRepair;

      // The ledger row IS the durable record of this divergence, and it surfaces as a gauge that
      // falls when the bucket heals. Publishing is opt-in because nothing consumes these events and
      // each one mints its own stream — see StreamIntegrityOptions.PublishReportEvents.
      if (!options.PublishReportEvents || reportsPublished >= reportCap) {
        continue;
      }
      reportsPublished++;
      await dispatcher.PublishAsync(new IntegrityDivergenceDetected {
        ReportStreamId = TrackedGuid.NewMedo().Value,
        OriginServiceId = message.OriginServiceId,
        OriginServiceName = message.OriginServiceName,
        TenantScope = origin.TenantScope,
        EventType = origin.EventType,
        AuditedStreamId = origin.StreamId,
        OriginCount = origin.EventCount,
        LocalCount = mine?.EventCount ?? 0,
        AutoRepairRequested = autoRepair,
      }).ConfigureAwait(false);
    }

    foreach (var ((tenantScope, eventType), tally) in divergenceTallies) {
      LogDivergence(logger, eventType, tenantScope, tally.Count, tally.SampleStreamId,
        message.OriginServiceName, tally.OriginTotal, tally.LocalTotal, tally.AnyAutoRepair);
    }
    // Only a CAP warrants the warning. With publishing disabled (the production default),
    // reportsPublished is always 0 and "N divergent, 0 reported" would fire on every comparison
    // that found anything — reading as data loss when the ledger row was written and the repair
    // went out. Disabled-by-choice is not truncation.
    if (options.PublishReportEvents && divergentSeen > reportsPublished) {
      LogDivergenceReportsCapped(logger, message.OriginServiceName, divergentSeen, reportsPublished);
    }

    // One directed request per divergent (tenant, type) — per-stream commands multiplied every
    // storm by the stream count for no selection benefit (the origin's WHERE takes a stream set).
    foreach (var ((tenantScope, eventType), streamIds) in repairBatches) {
      await _sendRepairRequestAsync(services, options, message, tenantScope, eventType, streamIds, cancellationToken)
        .ConfigureAwait(false);
    }

    // #80-B cursor-following: a non-null resume cursor means this window has MORE stream pages.
    // Without following, a lane wider than one page only ever audited its first
    // MaxDigestsPerManifest streams — the rest was never compared, never repaired, and the seal
    // never certified the window (the type-level seal rule requires a null cursor).
    await _followResumeCursorAsync(services, options, message, types, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Per-(origin, window) pages followed in the current audit burst. In-memory and per-process on
  /// purpose: the bound protects THIS replica's compare budget, a restart merely re-follows a few
  /// pages, and carrying a page counter on the wire would hand old peers a field they never echo —
  /// an unbounded chain, the exact failure the bound exists to stop. Entries expire by
  /// timestamp (a fresh audit round restarts the budget) and complete windows remove theirs.
  /// </summary>
  private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Origin, long Since, long Until), (int Pages, DateTimeOffset Last)> _pagesFollowed = new();

  /// <summary>
  /// Per-(origin, type) drill-down recency for the rotation fairness in the type-level handler.
  /// In-memory and per-process like <see cref="_pagesFollowed"/>: the bound being protected is
  /// THIS replica's selection fairness, and a restart merely resets the rotation.
  /// </summary>
  private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Origin, string EventType), DateTimeOffset> _lastDrilled = new();

  private async Task _followResumeCursorAsync(
      IServiceProvider services, StreamIntegrityOptions options, IntegrityManifest message,
      List<string> types, CancellationToken cancellationToken) {
    if (message.ComputedThrough is not long through) {
      return;   // legacy unwindowed answer — there is no window to page.
    }
    var key = (message.OriginServiceId, message.SinceSequence ?? 0, through);
    if (message.ResumeAfterStreamId is not Guid cursor) {
      _pagesFollowed.TryRemove(key, out _);   // window answered completely — budget resets.
      return;
    }
    var now = DateTimeOffset.UtcNow;
    var burstWindow = TimeSpan.FromMinutes(Math.Max(1, options.AuditIntervalMinutes));
    var entry = _pagesFollowed.TryGetValue(key, out var seen) && now - seen.Last < burstWindow
      ? seen
      : (Pages: 0, Last: now);
    if (entry.Pages >= Math.Max(0, options.MaxManifestPagesPerAudit)) {
      LogCursorFollowCapped(logger, message.OriginServiceName, entry.Pages);
      services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>()?.ManifestPagesCapped.Add(1,
        new KeyValuePair<string, object?>("origin", message.OriginServiceName));
      return;   // the rest of the lane re-audits from the seal next cycle.
    }

    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = options.RepairTopic
      ?? services.GetService<Whizbang.Core.Workers.TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
    // instanceProvider is guarded directly, not just via the value derived from it, so both the
    // compiler and the analyzer can see the later use cannot be null.
    if (instanceProvider is null || transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return;
    }
    // Directed or not at all — the same rule as the repair request and the drill-down.
    var originRequestTopic = services.GetService<IntegrityGapTracker>()?.GetRequestTopic(message.OriginServiceId);
    if (string.IsNullOrEmpty(originRequestTopic)) {
      LogCursorFollowSkippedNoOriginTopic(logger, message.OriginServiceName);
      return;
    }

    _pagesFollowed[key] = (entry.Pages + 1, now);
    services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>()?.ManifestPagesFollowed.Add(1,
      new KeyValuePair<string, object?>("origin", message.OriginServiceName));
    if (_pagesFollowed.Count > 256) {
      _pagesFollowed.Clear();   // windows advance; stale keys are waste, not state.
    }
    var envelope = new MessageEnvelope<RequestIntegrityManifest> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new RequestIntegrityManifest {
        RequesterService = requester,
        Topic = topic,
        EventTypes = types,
        Level = ManifestLevel.Streams,
        UseRecompute = message.Recomputed,
        // The follow-up stays inside the SAME window — a shifted window compares incomparable
        // folds — resuming exactly where the origin's answer stopped.
        Windowed = true,
        SinceSequence = message.SinceSequence ?? 0,
        UntilSequence = through,
        MaxDigests = options.MaxDigestsPerManifest,
        ResumeAfterStreamId = cursor,
      },
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestIntegrityManifest), instanceProvider.ToInfo(), now)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = message.OriginServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.ControlPlaneDestination.For(originRequestTopic, envelope.MessageId.Value, typeof(RequestIntegrityManifest)), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
    LogCursorFollowed(logger, entry.Pages + 1, message.OriginServiceName);
  }

  /// <summary>
  /// A1c: the type-level half of the hierarchical exchange. Compares the origin's per-(tenant,
  /// type) roll-ups against this consumer's; matching roll-ups prove EVERY stream bucket of the
  /// type complete — one comparison instead of thousands. Mismatched types escalate (capped) to a
  /// DIRECTED stream-level manifest request; reports only ever come from the stream-level compare.
  /// </summary>
  private async Task _handleTypeLevelAsync(
      IServiceProvider services, StreamIntegrityOptions options, IntegrityManifest message,
      List<string> types, TimeSpan settle, CancellationToken cancellationToken) {
    var coordinator = services.GetRequiredService<IWorkCoordinator>();
    var windowed = message.ComputedThrough is not null;
    IReadOnlyList<StreamDigest> local;
    if (message.ComputedThrough is long windowEnd) {
      // #80-B/C: the local fold covers the manifest's EXACT window — anything else compares
      // incomparable ranges and alarms on phantoms.
      var windowedLocal = await coordinator.ComputeTypeDigestsWindowedAsync(
        message.OriginServiceId, types, message.SinceSequence ?? 0, windowEnd, settle,
        cancellationToken).ConfigureAwait(false);
      if (windowedLocal is null) {
        LogWindowedCompareUnsupported(logger, message.OriginServiceName);
        return;
      }
      local = windowedLocal.Digests;
    } else {
      local = message.Recomputed
        ? await coordinator.ComputeTypeDigestsAsync(message.OriginServiceId, types, settle, cancellationToken).ConfigureAwait(false)
        : await _tableDigestsWithFallbackAsync(coordinator, message.OriginServiceId, types, settle, streamLevel: false, cancellationToken).ConfigureAwait(false);
    }
    var localByBucket = local.ToDictionary(d => (d.TenantScope, d.EventType));

    var mismatched = new List<string>();
    var bulkCandidates = new List<(StreamDigest Origin, StreamDigest? Mine, long Deficit)>();
    foreach (var origin in message.Digests) {
      localByBucket.TryGetValue((origin.TenantScope, origin.EventType), out var mine);
      if (mine is not null && mine.DigestLo == origin.DigestLo && mine.DigestHi == origin.DigestHi) {
        continue;
      }
      if (!message.Recomputed && !windowed && IntegrityDigestMath.IsInsideSettle(origin.UpdatedAt, mine?.UpdatedAt, settle)) {
        continue;   // windowed folds already exclude unsettled rows on both sides.
      }
      if (!mismatched.Contains(origin.EventType)) {
        mismatched.Add(origin.EventType);
      }
      // Bulk-deficit escalation candidates (WINDOWED roll-ups only — an unwindowed count spans
      // all history and a range-unbounded backfill is the re-ship-everything storm this whole
      // subsystem exists to avoid). Only a DEFICIT escalates, same taxonomy as the stream level.
      var deficit = origin.EventCount - (mine?.EventCount ?? 0);
      if (windowed && options.BulkBackfillThresholdEvents > 0 && deficit >= options.BulkBackfillThresholdEvents) {
        bulkCandidates.Add((origin, mine, deficit));
      }
    }
    if (mismatched.Count == 0) {
      // #80-C seal advance — only when the whole window PROVABLY passed: every bucket matched,
      // the answer was one complete chunk, and no resume cursor was pending. Chunks carry no
      // assembly protocol, so a multi-chunk window can never be certified from one chunk — it
      // still compared and repaired above, it just does not move the seal.
      if (windowed && message.ChunkCount == 1 && message.ResumeAfterStreamId is null
          && message.ComputedThrough is long through) {
        await coordinator.AdvanceIntegritySealAsync(message.OriginServiceId, through, cancellationToken)
          .ConfigureAwait(false);
        LogSealAdvanced(logger, message.OriginServiceName, through);
      }
      return;   // every type roll-up matches — the whole subscribed surface is provably complete.
    }

    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = options.RepairTopic
      ?? services.GetService<Whizbang.Core.Workers.TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
    if (instanceProvider is null || transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return;   // no drill-down infrastructure — the mismatch re-audits next cycle.
    }

    // Bulk-deficit escalation: a windowed (tenant, type) deficit at or past the threshold skips
    // the stream drill-down entirely — one state-only, range-bounded redelivery of the whole
    // type's window replaces days of 500-stream pages dripping 25 repairs per cycle. The
    // synthetic empty-stream ledger key gives the bulk ask the same attempt backoff every
    // stream-scoped repair gets, so audit cycles never re-ship a window already in flight.
    var bulkEscalated = new HashSet<string>(StringComparer.Ordinal);
    if (bulkCandidates.Count > 0 && options.RepairMode == IntegrityRepairMode.AutoRepairCapped) {
      var ledger = services.GetService<IIntegrityRepairLedger>()
        ?? (IIntegrityRepairLedger?)services.GetService<IntegrityRepairLedger>()
        ?? new IntegrityRepairLedger();
      var now = DateTimeOffset.UtcNow;
      var cooldown = TimeSpan.FromMinutes(options.DivergenceReportCooldownMinutes);
      var backoff = TimeSpan.FromSeconds(options.RepairRequestBackoffSeconds);
      var keys = bulkCandidates
        .Select(c => new IntegrityRepairLedger.DivergenceKey(message.OriginServiceId, c.Origin.TenantScope, c.Origin.EventType, Guid.Empty))
        .ToList();
      // Report before repair: the durable ledger only grants repairs for KNOWN divergences, and
      // the report row is the operator-facing record of the type-level deficit itself.
      var observations = new IntegrityReportObservation[bulkCandidates.Count];
      for (var i = 0; i < bulkCandidates.Count; i++) {
        var (origin, mine, _) = bulkCandidates[i];
        observations[i] = new IntegrityReportObservation(
          keys[i], origin.DigestLo, origin.DigestHi, mine?.DigestLo ?? 0, mine?.DigestHi ?? 0);
      }
      _ = await ledger.TryBeginReportBatchAsync(observations, now, cooldown, cancellationToken).ConfigureAwait(false);
      var grants = await ledger.TryBeginRepairBatchAsync(
        keys, now, backoff, options.MaxRepairAttemptsPerBucket, keys.Count, cancellationToken).ConfigureAwait(false);
      var metrics = services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
      for (var i = 0; i < bulkCandidates.Count; i++) {
        var (origin, _, deficit) = bulkCandidates[i];
        // Only a GRANTED ask excludes the type from the drill-down — that bulk backfill is
        // genuinely in flight and drilling beside it would double-ship the window. A DENIAL must
        // not exclude: the deny may be attempt EXHAUSTION (every ask burned into a broken
        // transport era, and a static origin signature never resets it), and shadow-banning the
        // type from the per-stream path too left the largest deficits permanently unrepairable
        // by every path (observed live). The per-stream keys carry their own backoff/attempts,
        // so a backoff-denied lane drilling down is bounded, not a storm.
        if (i >= grants.Count || !grants[i]) {
          continue;
        }
        bulkEscalated.Add(origin.EventType);
        metrics?.RepairsRequested.Add(1,
          new KeyValuePair<string, object?>("source", "bulk"),
          new KeyValuePair<string, object?>("origin", message.OriginServiceName));
        await _sendBulkBackfillRequestAsync(services, options, message, origin.TenantScope, origin.EventType, cancellationToken)
          .ConfigureAwait(false);
        LogBulkBackfillRequested(logger, origin.EventType, origin.TenantScope, deficit, message.OriginServiceName);
      }
    }

    // Least-recently-drilled first: taking the manifest's head every cycle starves every type
    // past the cap forever (deterministic order + deterministic truncation = the same N types
    // consume the budget while the largest deficits never get a single stream-level compare —
    // observed live). Per-process recency is enough: a restart merely resets fairness, and the
    // stable sort keeps manifest order among never-drilled types.
    var drillDown = mismatched
      .Where(t => !bulkEscalated.Contains(t))
      .OrderBy(t => _lastDrilled.TryGetValue((message.OriginServiceId, t), out var at) ? at : DateTimeOffset.MinValue)
      .Take(Math.Max(0, options.MaxDrillDownTypesPerAudit)).ToList();
    if (drillDown.Count == 0) {
      return;
    }
    var drilledAt = DateTimeOffset.UtcNow;
    foreach (var t in drillDown) {
      _lastDrilled[(message.OriginServiceId, t)] = drilledAt;
    }
    var envelope = new MessageEnvelope<RequestIntegrityManifest> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new RequestIntegrityManifest {
        RequesterService = requester,
        Topic = topic,
        EventTypes = drillDown,
        Level = ManifestLevel.Streams,
        UseRecompute = message.Recomputed,
        // #80-C: the drill-down inherits the window that disagreed — the stream-level exchange
        // (and the repairs it spawns) stay bounded to that range instead of re-shipping history.
        Windowed = windowed,
        SinceSequence = message.SinceSequence ?? 0,
        UntilSequence = message.ComputedThrough,
        MaxDigests = windowed ? options.MaxDigestsPerManifest : null,
      },
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestIntegrityManifest), instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown, DateTimeOffset.UtcNow)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = message.OriginServiceName,
    };
    // Directed or not at all — the same rule as the repair request (see
    // _sendRepairRequestAsync): without the origin's carried address the drill-down waits for a
    // checkpoint instead of broadcasting off the requester's own topic.
    var originRequestTopic = services.GetService<IntegrityGapTracker>()?.GetRequestTopic(message.OriginServiceId);
    if (string.IsNullOrEmpty(originRequestTopic)) {
      LogDrillDownSkippedNoOriginTopic(logger, message.OriginServiceName, drillDown.Count);
      return;
    }
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.ControlPlaneDestination.For(originRequestTopic, envelope.MessageId.Value, typeof(RequestIntegrityManifest)), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
    services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>()?.DrillDownsRequested.Add(1,
      new KeyValuePair<string, object?>("origin", message.OriginServiceName));
    LogDrillDown(logger, drillDown.Count, mismatched.Count, message.OriginServiceName);
  }

  /// <summary>Table reads, falling back to the recompute when the table has no rows — either
  /// nothing was received (recompute is equally empty, cheap) or the provider lacks the digest
  /// table (the DIM default returns empty; the recompute is the honest source).</summary>
  private static async Task<IReadOnlyList<StreamDigest>> _tableDigestsWithFallbackAsync(
      IWorkCoordinator coordinator, Guid originServiceId, List<string> types, TimeSpan settle,
      bool streamLevel, CancellationToken cancellationToken) {
    var table = streamLevel
      ? await coordinator.GetStreamDigestsAsync(originServiceId, types, cancellationToken).ConfigureAwait(false)
      : await coordinator.GetTypeDigestsAsync(originServiceId, types, cancellationToken).ConfigureAwait(false);
    if (table.Count > 0) {
      return table;
    }
    // The recompute fallback (unpopulated digest lane) matches the requested level at the store:
    // a types-level fallback materialized per-stream has memory-killed consumers.
    return streamLevel
      ? await coordinator.ComputeStreamDigestsAsync(originServiceId, types, settle, cancellationToken).ConfigureAwait(false)
      : await coordinator.ComputeTypeDigestsAsync(originServiceId, types, settle, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The bulk half of #80-C's range-bounded backfill: ONE state-only redelivery of a whole
  /// (tenant, type) window — no stream list, because the deficit spans more streams than the
  /// per-stream path can drip through in any reasonable number of cycles. State-only is
  /// load-bearing: backfilled history builds state and never re-fires trigger receptors.
  /// </summary>
  private async Task _sendBulkBackfillRequestAsync(
      IServiceProvider services, StreamIntegrityOptions options,
      IntegrityManifest manifest, string? tenantScope, string eventType,
      CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = options.RepairTopic
      ?? services.GetService<Whizbang.Core.Workers.TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
    if (instanceProvider is null || transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return;
    }
    var originRequestTopic = services.GetService<IntegrityGapTracker>()?.GetRequestTopic(manifest.OriginServiceId);
    if (string.IsNullOrEmpty(originRequestTopic)) {
      LogRepairSkippedNoOriginTopic(logger, manifest.OriginServiceName, eventType, 0);
      return;
    }

    var envelope = new MessageEnvelope<RequestRedeliveryCommand> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new RequestRedeliveryCommand {
        TenantScope = tenantScope,
        EventTypes = [eventType],
        // No stream filter — the whole type's window backfills; a stream list IS the drip this
        // path exists to skip. The origin's pump pages the selection and caps bytes per bundle.
        StreamIds = null,
        RequesterService = requester,
        Topic = topic,
        // Same [since, until) → exclusive-floor/inclusive-ceiling mapping as the per-stream
        // repair — the bulk ask stays bounded to the window that disagreed.
        FromCommitSequence = manifest.SinceSequence is long since && since > 0 ? since - 1 : null,
        ToCommitSequence = manifest.ComputedThrough is long through ? through - 1 : null,
        StateOnly = true,
      },
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestRedeliveryCommand), instanceProvider.ToInfo(), DateTimeOffset.UtcNow)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = manifest.OriginServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.ControlPlaneDestination.For(originRequestTopic, envelope.MessageId.Value, typeof(RequestRedeliveryCommand)), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
  }

  private async Task _sendRepairRequestAsync(
      IServiceProvider services, StreamIntegrityOptions options,
      IntegrityManifest manifest, string? tenantScope, string eventType, List<Guid> streamIds,
      CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = options.RepairTopic
      ?? services.GetService<Whizbang.Core.Workers.TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
    if (instanceProvider is null || transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return;   // report already published; the repair rides the next cycle when infra exists.
    }
    // Directed or not at all: without the origin-carried request address the ONLY other topic on
    // hand is the requester's own — publishing there fanned the request out to every service on
    // the shared topic (and back to the requester itself), which is how a repair loop became an
    // all-to-all flood. The origin's next checkpoint teaches the address; the ledger's backoff
    // re-offers the repair then.
    var originRequestTopic = services.GetService<IntegrityGapTracker>()?.GetRequestTopic(manifest.OriginServiceId);
    if (string.IsNullOrEmpty(originRequestTopic)) {
      LogRepairSkippedNoOriginTopic(logger, manifest.OriginServiceName, eventType, streamIds.Count);
      return;
    }

    var envelope = new MessageEnvelope<RequestRedeliveryCommand> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new RequestRedeliveryCommand {
        TenantScope = tenantScope,
        EventTypes = [eventType],
        StreamIds = streamIds,
        RequesterService = requester,
        Topic = topic,
        // #80-C range-bounded backfill: a deficit found comparing a WINDOW is a deficit in that
        // window — the origin re-ships the slice, not the streams' whole history. [since, until)
        // maps to the command's exclusive-floor / inclusive-ceiling pair; legacy (unwindowed)
        // manifests leave both null, the pre-existing whole-history semantics.
        FromCommitSequence = manifest.SinceSequence is long since && since > 0 ? since - 1 : null,
        ToCommitSequence = manifest.ComputedThrough is long through ? through - 1 : null,
      },
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestRedeliveryCommand), instanceProvider.ToInfo(), DateTimeOffset.UtcNow)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = manifest.OriginServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.ControlPlaneDestination.For(originRequestTopic, streamIds[0], typeof(RequestRedeliveryCommand)), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Running per-(tenant, type) totals so divergence is logged once per bucket instead of once per
  /// stream. Hundreds of near-identical lines cost real work on the thread that owes the liveness
  /// probe an answer, and bury the signal an operator is actually looking for.
  /// </summary>
  private sealed class _divergenceTally {
    public int Count;
    public long OriginTotal;
    public long LocalTotal;
    public bool AnyAutoRepair;
    public Guid SampleStreamId;
  }

  [LoggerMessage(EventId = 54, Level = LogLevel.Warning,
    Message = "AUDIT divergence: {EventType} (tenant {TenantScope}) — {DivergentStreams} stream(s) vs origin " +
              "'{OriginServiceName}' (e.g. {SampleStreamId}); origin {OriginTotal}, local {LocalTotal} " +
              "(autoRepair={AutoRepairRequested})")]
  static partial void LogDivergence(ILogger logger, string eventType, string? tenantScope, int divergentStreams,
    Guid sampleStreamId, string originServiceName, long originTotal, long localTotal, bool autoRepairRequested);

  [LoggerMessage(EventId = 57, Level = LogLevel.Warning,
    Message = "AUDIT divergence reports capped for origin '{OriginServiceName}': {DivergentStreams} divergent, " +
              "{Reported} reported. The remainder stays unhealed in the ledger and is re-offered next comparison.")]
  static partial void LogDivergenceReportsCapped(ILogger logger, string originServiceName, int divergentStreams,
    int reported);

  [LoggerMessage(EventId = 55, Level = LogLevel.Warning,
    Message = "AUDIT type-level mismatch vs origin '{OriginServiceName}': drilling down to stream level for " +
              "{DrillDownCount} of {MismatchedCount} mismatched type(s)")]
  static partial void LogDrillDown(ILogger logger, int drillDownCount, int mismatchedCount, string originServiceName);

  [LoggerMessage(EventId = 56, Level = LogLevel.Information,
    Message = "Repair request to '{OriginServiceName}' withheld ({EventType}, {StreamCount} stream(s)) — " +
              "no origin-carried request address yet; the origin's next checkpoint teaches it")]
  static partial void LogRepairSkippedNoOriginTopic(ILogger logger, string originServiceName, string eventType, int streamCount);

  [LoggerMessage(EventId = 58, Level = LogLevel.Information,
    Message = "Drill-down to '{OriginServiceName}' withheld ({TypeCount} type(s)) — " +
              "no origin-carried request address yet; the origin's next checkpoint teaches it")]
  static partial void LogDrillDownSkippedNoOriginTopic(ILogger logger, string originServiceName, int typeCount);

  [LoggerMessage(EventId = 59, Level = LogLevel.Warning,
    Message = "Windowed manifest from '{OriginServiceName}' skipped — this engine cannot fold the manifest's " +
              "window, and comparing incomparable ranges would alarm on phantoms")]
  static partial void LogWindowedCompareUnsupported(ILogger logger, string originServiceName);

  [LoggerMessage(EventId = 60, Level = LogLevel.Information,
    Message = "Integrity seal vs origin '{OriginServiceName}' advanced through sequence {Through} — " +
              "the window audited clean and complete; future audits start here")]
  static partial void LogSealAdvanced(ILogger logger, string originServiceName, long through);

  [LoggerMessage(EventId = 61, Level = LogLevel.Information,
    Message = "Origin '{OriginServiceName}' history generation changed to {Generation} — a legitimate " +
              "mutation (close/reclassify); seal reset, this comparison round skipped, re-verifying from the beginning next audit")]
  static partial void LogGenerationChanged(ILogger logger, string originServiceName, long generation);

  [LoggerMessage(EventId = 62, Level = LogLevel.Debug,
    Message = "Manifest chunk from '{OriginServiceName}' ({DigestCount} digest(s)) skipped — a comparison " +
              "is already in flight, and queued chunks hold their payloads in memory; its buckets re-audit next cycle")]
  static partial void LogCompareBusySkipped(ILogger logger, string originServiceName, int digestCount);

  [LoggerMessage(EventId = 63, Level = LogLevel.Information,
    Message = "AUDIT following resume cursor (page {Page}) from '{OriginServiceName}' — the window has more stream pages")]
  static partial void LogCursorFollowed(ILogger logger, int page, string originServiceName);

  [LoggerMessage(EventId = 64, Level = LogLevel.Information,
    Message = "AUDIT cursor-following capped at {PagesFollowed} page(s) for '{OriginServiceName}' — " +
              "the rest of the window re-audits from the seal next cycle")]
  static partial void LogCursorFollowCapped(ILogger logger, string originServiceName, int pagesFollowed);

  [LoggerMessage(EventId = 65, Level = LogLevel.Debug,
    Message = "AUDIT cursor-follow to '{OriginServiceName}' skipped — no origin request topic learned yet")]
  static partial void LogCursorFollowSkippedNoOriginTopic(ILogger logger, string originServiceName);

  [LoggerMessage(EventId = 66, Level = LogLevel.Warning,
    Message = "AUDIT bulk backfill requested: {EventType} (tenant {TenantScope}) — {Deficit} event(s) missing " +
              "vs origin '{OriginServiceName}' in the audited window; ONE state-only range-bounded redelivery replaces the per-stream drip")]
  static partial void LogBulkBackfillRequested(ILogger logger, string eventType, string? tenantScope, long deficit, string originServiceName);
}

/// <summary>
/// Registers the Phase A manifest receptors with <see cref="IReceptorRegistry"/> at startup — the
/// same runtime-registration rationale as every framework receptor in this assembly.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs</tests>
internal sealed class IntegrityManifestReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrityManifestRequestReceptor> requestLogger,
    ILogger<IntegrityManifestReceptor> manifestLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    if (registry is null) {
      return Task.CompletedTask;
    }
    var requestReceptor = new IntegrityManifestRequestReceptor(scopeFactory, requestLogger);
    registry.Register<RequestIntegrityManifest>(requestReceptor, LifecycleStage.LocalImmediateInline);
    registry.Register<RequestIntegrityManifest>(requestReceptor, LifecycleStage.PreOutboxInline);
    registry.Register<RequestIntegrityManifest>(requestReceptor, LifecycleStage.PostInboxInline);
    var manifestReceptor = new IntegrityManifestReceptor(scopeFactory, manifestLogger);
    registry.Register<IntegrityManifest>(manifestReceptor, LifecycleStage.LocalImmediateInline);
    registry.Register<IntegrityManifest>(manifestReceptor, LifecycleStage.PreOutboxInline);
    registry.Register<IntegrityManifest>(manifestReceptor, LifecycleStage.PostInboxInline);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
