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
    if (!message.UseRecompute) {
      digests = message.Level == ManifestLevel.Types
        ? await coordinator.GetTypeDigestsAsync(null, message.EventTypes, cancellationToken).ConfigureAwait(false)
        : await coordinator.GetStreamDigestsAsync(null, message.EventTypes, cancellationToken).ConfigureAwait(false);
      // Empty table read: either genuinely nothing emitted OR the provider has no digest table
      // (the DIM default). The recompute distinguishes them — cheap when truly empty.
      recomputed = digests.Count == 0;
    } else {
      digests = [];
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

    var originServiceId = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var originName = instanceProvider?.ServiceName ?? string.Empty;
    // All chunks of one manifest share the manifest stream (the origin id) as their session —
    // session-enabled subscriptions dead-letter sessionless deliveries, and a shared session
    // keeps chunk order.
    var destination = Whizbang.Core.Transports.ControlPlaneDestination.For(message.Topic, originServiceId, typeof(IntegrityManifest));
    var chunks = 0;

    for (var offset = 0; offset < digests.Count; offset += options.MaxDigestsPerManifest) {
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
        },
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTimeOffset.UtcNow,
            ServiceInstance = instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown
          }
        ],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
        Target = message.RequesterService,
      };
      var serialized = serializer.SerializeEnvelope(envelope);
      await transport.PublishAsync(serialized.JsonEnvelope, destination, serialized.EnvelopeType,
        cancellationToken: cancellationToken).ConfigureAwait(false);
      chunks++;
    }
    services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>()?.ManifestChunksSent.Add(chunks,
      new KeyValuePair<string, object?>("level", message.Level.ToString()));
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
    await _compareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      await _handleCoreAsync(message, cancellationToken).ConfigureAwait(false);
    } finally {
      _compareGate.Release();
    }
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

    var types = message.Digests.Select(d => d.EventType).Distinct(StringComparer.Ordinal).ToList();
    var settle = TimeSpan.FromMinutes(options.AuditSettleWindowMinutes);

    if (message.Level == ManifestLevel.Types) {
      await _handleTypeLevelAsync(services, options, message, types, settle, cancellationToken).ConfigureAwait(false);
      return;
    }

    // Stream level. A sweep manifest (Recomputed) compares recompute-to-recompute — the pre-A1c
    // semantics, covering busy buckets; a table manifest compares table-to-table with settle-skip.
    var local = message.Recomputed
      ? await coordinator.ComputeStreamDigestsAsync(message.OriginServiceId, types, settle, cancellationToken).ConfigureAwait(false)
      : await _tableDigestsWithFallbackAsync(coordinator, message.OriginServiceId, types, settle, streamLevel: true, cancellationToken).ConfigureAwait(false);
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
    foreach (var origin in message.Digests) {
      localByBucket.TryGetValue((origin.TenantScope, origin.EventType, origin.StreamId), out var mine);
      var key = new IntegrityRepairLedger.DivergenceKey(
        message.OriginServiceId, origin.TenantScope, origin.EventType, origin.StreamId);
      if (mine is not null && mine.DigestLo == origin.DigestLo && mine.DigestHi == origin.DigestHi) {
        await ledger.MarkHealedAsync(key, cancellationToken).ConfigureAwait(false);   // provably complete — a later divergence is a fresh incident.
        continue;
      }
      if (!message.Recomputed && IntegrityDigestMath.IsInsideSettle(origin.UpdatedAt, mine?.UpdatedAt, settle)) {
        continue;   // the bucket changed inside the settle window — in-flight, not divergence.
      }

      metrics?.DivergencesDetected.Add(1,
        new KeyValuePair<string, object?>("origin", message.OriginServiceName),
        new KeyValuePair<string, object?>("event_type", origin.EventType));
      var shouldReport = await ledger.TryBeginReportAsync(
        key, origin.DigestLo, origin.DigestHi, mine?.DigestLo ?? 0, mine?.DigestHi ?? 0, now, cooldown,
        cancellationToken).ConfigureAwait(false);
      var autoRepair = options.RepairMode == IntegrityRepairMode.AutoRepairCapped && repairBudget > 0
        && await ledger.TryBeginRepairAsync(key, now, backoff, options.MaxRepairAttemptsPerBucket,
             cancellationToken).ConfigureAwait(false);
      if (autoRepair) {
        repairBudget--;
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
    if (divergentSeen > reportsPublished) {
      LogDivergenceReportsCapped(logger, message.OriginServiceName, divergentSeen, reportsPublished);
    }

    // One directed request per divergent (tenant, type) — per-stream commands multiplied every
    // storm by the stream count for no selection benefit (the origin's WHERE takes a stream set).
    foreach (var ((tenantScope, eventType), streamIds) in repairBatches) {
      await _sendRepairRequestAsync(services, options, message, tenantScope, eventType, streamIds, cancellationToken)
        .ConfigureAwait(false);
    }
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
    var local = message.Recomputed
      ? await coordinator.ComputeTypeDigestsAsync(message.OriginServiceId, types, settle, cancellationToken).ConfigureAwait(false)
      : await _tableDigestsWithFallbackAsync(coordinator, message.OriginServiceId, types, settle, streamLevel: false, cancellationToken).ConfigureAwait(false);
    var localByBucket = local.ToDictionary(d => (d.TenantScope, d.EventType));

    var mismatched = new List<string>();
    foreach (var origin in message.Digests) {
      localByBucket.TryGetValue((origin.TenantScope, origin.EventType), out var mine);
      if (mine is not null && mine.DigestLo == origin.DigestLo && mine.DigestHi == origin.DigestHi) {
        continue;
      }
      if (!message.Recomputed && IntegrityDigestMath.IsInsideSettle(origin.UpdatedAt, mine?.UpdatedAt, settle)) {
        continue;
      }
      if (!mismatched.Contains(origin.EventType)) {
        mismatched.Add(origin.EventType);
      }
    }
    if (mismatched.Count == 0) {
      return;   // every type roll-up matches — the whole subscribed surface is provably complete.
    }

    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = options.RepairTopic
      ?? services.GetService<Whizbang.Core.Workers.TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
    if (transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return;   // no drill-down infrastructure — the mismatch re-audits next cycle.
    }

    var drillDown = mismatched.Take(Math.Max(0, options.MaxDrillDownTypesPerAudit)).ToList();
    if (drillDown.Count == 0) {
      return;
    }
    var envelope = new MessageEnvelope<RequestIntegrityManifest> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new RequestIntegrityManifest {
        RequesterService = requester,
        Topic = topic,
        EventTypes = drillDown,
        Level = ManifestLevel.Streams,
        UseRecompute = message.Recomputed,
      },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown
        }
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
    if (transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
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
      },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown
        }
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

  [LoggerMessage(EventId = 57, Level = LogLevel.Information,
    Message = "Drill-down to '{OriginServiceName}' withheld ({TypeCount} type(s)) — " +
              "no origin-carried request address yet; the origin's next checkpoint teaches it")]
  static partial void LogDrillDownSkippedNoOriginTopic(ILogger logger, string originServiceName, int typeCount);
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
