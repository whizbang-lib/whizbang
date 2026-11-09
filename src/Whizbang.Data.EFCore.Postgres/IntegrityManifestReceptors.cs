// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
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
/// <summary>
/// Metric dimension names shared by the manifest receptors in this file. Only the ones that
/// recur are named here; a dimension used once reads better spelled at its use.
/// </summary>
internal static class IntegrityManifestTags {
  /// <summary>Names the service a manifest message came from.</summary>
  internal const string ORIGIN = "origin";

  /// <summary>Why a deficit bucket was deferred instead of added to the paced repair drain.</summary>
  internal const string DEFERRAL_REASON = "reason";
}

/// <summary>
/// The decisions every integrity receptor makes the same way before it asks an origin to redeliver:
/// which repair ledger bounds the asking, which topic the redelivery is sent back to, and which
/// commit-sequence window the request covers. Taken once here so the checkpoint and manifest
/// receptors cannot drift apart on any of them.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairSupportTests.cs</tests>
internal static class IntegrityRepairSupport {
  /// <summary>
  /// The repair ledger: the registered <see cref="IIntegrityRepairLedger"/> (the coordinator-backed,
  /// fleet-wide one) when a host has one, else the registered in-process
  /// <see cref="IntegrityRepairLedger"/>, else a fresh in-process ledger so an unwired host still
  /// gets bounded asking rather than none.
  /// </summary>
  /// <param name="services">The scope the receptor resolves from.</param>
  /// <returns>The ledger to consult.</returns>
  internal static IIntegrityRepairLedger ResolveLedger(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return services.GetService<IIntegrityRepairLedger>()
      ?? (IIntegrityRepairLedger?)services.GetService<IntegrityRepairLedger>()
      ?? new IntegrityRepairLedger();
  }

  /// <summary>
  /// The topic a redelivery is sent back to: the configured <see cref="StreamIntegrityOptions.RepairTopic"/>, else this
  /// service's own command inbox, else its first consumer destination (#2078), else null, which the callers read as
  /// "no return address, skip the request".
  /// </summary>
  /// <param name="options">The stream-integrity options.</param>
  /// <param name="services">The scope the receptor resolves from.</param>
  /// <returns>The reply topic, or null when the service has none.</returns>
  internal static string? ResolveRepairTopic(StreamIntegrityOptions options, IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(services);
    if (options.RepairTopic is not null) {
      return options.RepairTopic;
    }
    // #2078: the one rule every integrity component uses: this service's own command inbox when it has one.
    return Whizbang.Core.Workers.RepairTopicResolver.Resolve(options.RepairTopic, services);
  }

  /// <summary>
  /// Maps a manifest's compared window <c>[since, until)</c> onto a redelivery command's
  /// exclusive-floor / inclusive-ceiling pair (#80-C range-bounded backfill). A manifest without a
  /// window, or one starting at the beginning, leaves that side null: the whole-history semantics.
  /// </summary>
  /// <param name="manifest">The manifest whose window disagreed.</param>
  /// <returns>The command's <c>FromCommitSequence</c> and <c>ToCommitSequence</c>.</returns>
  internal static (long? From, long? To) RedeliveryWindow(IntegrityManifest manifest) {
    ArgumentNullException.ThrowIfNull(manifest);
    long? from = manifest.SinceSequence is long since && since > 0 ? since - 1 : null;
    long? to = manifest.ComputedThrough is long through ? through - 1 : null;
    return (from, to);
  }
}

/// <summary>
/// The origin's bound on one windowed audit answer (#2107): at most
/// <see cref="StreamIntegrityOptions.MaxAuditWindowSequences"/> commit sequences from the window's start.
/// </summary>
/// <docs>resilience/stream-integrity#windowed-manifests</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs</tests>
internal static class AuditWindowCap {
  /// <summary>
  /// The exclusive end to read the window to, and the cap's end when the cap is what shortened it (null when the asked
  /// end was already inside the cap, or the cap's end saturates at the largest sequence).
  /// </summary>
  /// <param name="since">The window's inclusive start.</param>
  /// <param name="until">The asked exclusive end; null = through the settled head.</param>
  /// <param name="maxSequences">The cap, at least 1 (validated at host start).</param>
  /// <returns>The end to read to and, when the cap applied, the cap's end.</returns>
  internal static (long Until, long? CapEnd) Clamp(long since, long? until, int maxSequences) {
    if (since > long.MaxValue - maxSequences) {
      return (until ?? long.MaxValue, null);   // the cap's end would pass the largest sequence: nothing to shorten.
    }
    var capEnd = since + maxSequences;
    return until is long asked && asked <= capEnd ? (asked, null) : (capEnd, capEnd);
  }
}

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

  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Answering a request chooses between the windowed digest path, a full recompute and the stored digests, each of which can come back empty and fall through to the next, then pages the answer. The fall-through order is the method.")]
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
    var eventTypes = await _requestedTypesAsync(coordinator, message, cancellationToken).ConfigureAwait(false);
    IReadOnlyList<StreamDigest> digests;
    var recomputed = message.UseRecompute;
    long? computedThrough = null;
    Guid? resumeAfter = null;
    var answeredWindowed = false;
    var windowCapped = false;

    if (message.Windowed) {
      // #80-B negotiated scope: answer only [SinceSequence, UntilSequence) — epoch-served, so the
      // cost is the open window, not the store. A null result means the engine cannot window —
      // the honest fallback is the legacy full answer below (correct, just unbounded) — never a
      // fabricated watermark the engine cannot stand behind.
      var (until, capEnd) = AuditWindowCap.Clamp(message.SinceSequence, message.UntilSequence, options.MaxAuditWindowSequences);
      var windowed = message.Level == ManifestLevel.Types
        ? await coordinator.ComputeTypeDigestsWindowedAsync(
            null, eventTypes, message.SinceSequence, until, settle,
            cancellationToken).ConfigureAwait(false)
        : await coordinator.ComputeStreamDigestsWindowedAsync(
            null, eventTypes, message.SinceSequence, until,
            message.ResumeAfterStreamId, message.MaxDigests ?? options.MaxDigestsPerManifest, settle,
            cancellationToken).ConfigureAwait(false);
      if (windowed is not null) {
        if (windowed.Digests.Count == 0 && windowed.ComputedThrough <= message.SinceSequence) {
          return;   // nothing settled beyond the asker's watermark — no progress to report.
        }
        digests = windowed.Digests;
        computedThrough = windowed.ComputedThrough;
        // #2107: the answer stopped at the cap, not at the settled head: more settled history follows.
        windowCapped = capEnd is long cap && windowed.ComputedThrough == cap;
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
          ? await coordinator.GetTypeDigestsAsync(null, eventTypes, cancellationToken).ConfigureAwait(false)
          : await coordinator.GetStreamDigestsAsync(null, eventTypes, cancellationToken).ConfigureAwait(false);
        // Empty table read: either genuinely nothing emitted OR the provider has no digest table
        // (the DIM default). The recompute distinguishes them — cheap when truly empty.
        recomputed = digests.Count == 0;
      }
      if (recomputed) {
        // Types-level answers roll up AT THE STORE — materializing one row per stream to answer a
        // types-level request has memory-killed origins with large stores.
        digests = message.Level == ManifestLevel.Types
          ? await coordinator.ComputeTypeDigestsAsync(null, eventTypes, settle, cancellationToken).ConfigureAwait(false)
          : await coordinator.ComputeStreamDigestsAsync(null, eventTypes, settle, cancellationToken).ConfigureAwait(false);
      }
      // #2008: only owned types are this origin's emissions; the rest of the local lane was received from its authors.
      digests = _ownedOnly(services, digests);
      if (digests.Count == 0) {
        return;   // nothing this origin emitted for those types — silence, not an empty manifest.
      }
    } else {
      // A windowed answer still publishes when the filter leaves nothing: only an answer carries the watermark.
      digests = _ownedOnly(services, digests);
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
    var destination = Whizbang.Core.Transports.IntegrityControlDestination.For(message.Topic, originServiceId, typeof(IntegrityManifest), options);
    var chunks = 0;

    // do-while: a WINDOWED quiet window (zero digests, watermark advanced) must still publish —
    // only an answer can carry the watermark, and silence would freeze the asker's seal forever
    // on a quiet type. The legacy path never reaches here empty.
    var totalChunks = Math.Max(1, (digests.Count + options.MaxDigestsPerManifest - 1) / options.MaxDigestsPerManifest);
    var offset = 0;
    do {
      var chunk = digests.Skip(offset).Take(options.MaxDigestsPerManifest).ToList();
      var envelope = new MessageEnvelope<IntegrityManifest> {
        Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
        MessageId = new MessageId(TrackedGuid.New()),
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
          WindowCapped = windowCapped,
          WindowNumber = message.WindowNumber,
          Recheck = message.Recheck,
        },
        Hops = [
          Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(IntegrityManifest), instanceProvider, DateTimeOffset.UtcNow)
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

  private static IReadOnlyList<StreamDigest> _ownedOnly(IServiceProvider services, IReadOnlyList<StreamDigest> digests) {
    var owned = services.GetService<IOptions<Whizbang.Core.Routing.RoutingOptions>>()?.Value.OwnedDomains;
    return owned is not { Count: > 0 }
      ? digests
      : [.. digests.Where(d => Whizbang.Core.Routing.OwnedNamespaceMatcher.IsLocalEventType(d.EventType, owned))];
  }

  /// <summary>
  /// The types to answer for: the requester's <see cref="RequestIntegrityManifest.EventTypes"/>, plus, for each
  /// namespace it absorbs (#2058), every type this origin audits in that namespace. Without namespaces the request's
  /// list is answered as it came, null (every type) included, so an older requester is answered as before.
  /// </summary>
  private static async Task<IReadOnlyList<string>?> _requestedTypesAsync(
      IWorkCoordinator coordinator, RequestIntegrityManifest message, CancellationToken cancellationToken) {
    if (message.EventNamespaces is not { Count: > 0 } namespaces) {
      return message.EventTypes;
    }
    var absorbed = new HashSet<string>(namespaces, StringComparer.OrdinalIgnoreCase);
    var own = await coordinator.GetOwnAuditedEventTypesAsync(cancellationToken).ConfigureAwait(false);
    return [.. (message.EventTypes ?? [])
      .Concat(own.Where(t => TypeNameFormatter.GetPayloadNamespace(t) is { } ns && absorbed.Contains(ns)))
      .Distinct(StringComparer.Ordinal)];
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
  /// #2103: the most named events one repair request carries. A larger missing set is paged into several requests, so
  /// one request never grows with the divergence.
  /// </summary>
  internal const int MAX_NAMED_EVENTS_PER_REQUEST = 500;

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
    if (message.Digests.Count == 0 && !_isQuietWindow(message)) {
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
        new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName));
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

  /// <summary>
  /// A windowed type-level answer with no digests (#2107): the origin holds no event of the asked types in the window.
  /// It is still compared, because only its watermark can move the seal past a window with nothing to audit, and a
  /// capped window like that would otherwise be asked for again on every audit.
  /// </summary>
  private static bool _isQuietWindow(IntegrityManifest message) =>
    message.Digests.Count == 0 && message.Level == ManifestLevel.Types && message.ComputedThrough is not null;

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

  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Comparing a manifest decides first whether to act at all (collaborators wired, not our own manifest, generation coherent, the level), then per digest whether it matches, whether it falls inside the settle window, and whether the difference is a deficit worth stamping for repair. Each stage narrows the set the next one walks, and the settle rule needs both sides' timestamps.")]
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

    // #2058: compare only what the receive boundary keeps (what this service consumes, or a namespace it absorbs). An
    // origin answering an older, wider request still names other types; a divergence in one of them can never heal,
    // because its redelivery is discarded on receive.
    var keeps = ConsumedMessageTypes.Keeps(services);
    var kept = message.Digests.Where(d => keeps(d.EventType)).ToList();
    if (kept.Count == 0 && !_isQuietWindow(message)) {
      return;
    }
    if (kept.Count < message.Digests.Count) {
      message = message with { Digests = kept };
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

    // #2032: an older origin may still advertise types another service owns, or types this service owns itself; it is
    // not authoritative for them, and repair is asked of the owner, so those digests are not compared here (a ledger row
    // they left would never clear).
    var ownership = new IntegrityDomainOwnership(
      await coordinator.GetIntegrityDomainClaimsAsync(cancellationToken).ConfigureAwait(false));
    var ownDomains = services.GetService<IOptions<Whizbang.Core.Routing.RoutingOptions>>()?.Value.OwnedDomains
      ?? (IReadOnlyCollection<string>)[];
    if (message.Digests.Any(d => ownership.IsNotAuthoritative(d.EventType, message.OriginServiceId, ownDomains))) {
      message = message with {
        Digests = [.. message.Digests.Where(d => !ownership.IsNotAuthoritative(d.EventType, message.OriginServiceId, ownDomains))],
      };
      if (message.Digests.Count == 0) {
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
    // #2102: a windowed fold counts an event by its origin sequence, and one held without it reads as missing. The
    // disagreeing streams' sequence-less events are folded (bounded by the chunk, and paid only when a bucket disagrees)
    // and placed by identity: they complete the window, bound it, or leave it undecided.
    var heldByBucket = message.ComputedThrough is null
      ? []
      : await _unsequencedByBucketAsync(coordinator, message, localByBucket, settle, cancellationToken).ConfigureAwait(false);

    // Convergence bounding: the ledger suppresses re-reports of unchanged divergence inside the
    // cooldown and backs off repair re-requests per bucket — a persistent divergence (origin
    // down, damaged bucket) must trickle, not storm. Unregistered ledger (bare tests) degrades
    // to per-call state: everything reports once per manifest, still batched and still directed.
    // Prefer a durable ledger. The in-memory one is per-process and dies on restart, which is
    // sound only while restarts are rare — and here the report storm is what CAUSES the restarts,
    // so every boot cleared the state that would have suppressed it. It is also per-replica, so
    // each pod reported the same divergence independently.
    var ledger = IntegrityRepairSupport.ResolveLedger(services);
    var cooldown = TimeSpan.FromMinutes(options.DivergenceReportCooldownMinutes);
    var backoff = TimeSpan.FromSeconds(options.RepairRequestBackoffSeconds);
    var now = DateTimeOffset.UtcNow;
    var metrics = services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
    var repairBudget = options.MaxAutoRepairRequestsPerAudit;
    var repairBatches = new Dictionary<(string? TenantScope, string EventType), List<(Guid Stream, Guid? Missing)>>();
    var divergenceTallies = new Dictionary<(string? TenantScope, string EventType), DivergenceTally>();
    var reportCap = Math.Max(1, options.MaxDivergenceReportsPerManifest);
    var reportsPublished = 0;
    var divergentSeen = 0;

    // PASS 1 — pure memory: classify every bucket. The ledger is then consulted in THREE batch
    // round trips (healed / report / repair) instead of up to two PER BUCKET. The per-bucket
    // consult made each chunk's comparison seconds long — slower than manifests arrive — and
    // every waiting manifest held its deserialized payload behind the compare gate until the
    // process died (observed live, fleet-wide).
    var healedKeys = new List<IntegrityRepairLedger.DivergenceKey>();
    var undecidedKeys = new List<IntegrityRepairLedger.DivergenceKey>();
    var divergent = new List<(StreamDigest Origin, StreamDigest? Mine, bool IsDeficit, string Reason)>();
    foreach (var origin in message.Digests) {
      var bucket = (origin.TenantScope, origin.EventType, origin.StreamId);
      localByBucket.TryGetValue(bucket, out var placed);
      heldByBucket.TryGetValue(bucket, out var sequenceless);
      var placement = IntegrityDigestMath.PlaceHeld(origin, placed, sequenceless);
      var mine = placement.Held;
      var key = new IntegrityRepairLedger.DivergenceKey(message.OriginServiceId, origin.TenantScope, origin.EventType, origin.StreamId);
      if (placement.Verdict == HeldWindowVerdict.Matches) {
        // provably complete — a later divergence is a fresh incident.
        healedKeys.Add(key);
        continue;
      }
      if (!message.Recomputed && IntegrityDigestMath.IsInsideSettle(origin.UpdatedAt, mine?.UpdatedAt, settle)) {
        continue;   // the bucket changed inside the settle window — in-flight, not divergence.
      }
      if (placement.Verdict == HeldWindowVerdict.Undecided) {
        // #2102: the window cannot confirm a deficit, so none is kept; the full-history compare decides the stream.
        undecidedKeys.Add(key);
        continue;
      }

      // #80-C taxonomy — only a DEFICIT is repairable. Equal counts with differing folds mean the
      // consumer holds the same NUMBER of events with different IDENTITY: redelivery re-ships what
      // it already has, dedup drops it, the fold never moves — a repair loop that can never
      // converge (observed live as an unbounded redelivery storm). A local SURPLUS cannot be fixed
      // by asking the origin for more, and local history is never auto-deleted on a remote's
      // say-so. Both alarm; neither repairs.
      var localCount = mine?.EventCount ?? 0;
      var isDeficit = localCount < origin.EventCount;
      var reason = (isDeficit, localCount == origin.EventCount) switch {
        (true, _) => "deficit",
        (false, true) => "identity_mismatch",
        _ => "local_extra",
      };
      divergent.Add((origin, mine, isDeficit, reason));
    }

    // #2115: the rows this comparison removed, which a re-check's answer counts (a ledger that cannot say what it removed
    // counts nothing, never a guess).
    var rowsCleared = 0;
    if (undecidedKeys.Count > 0) {
      // Forgotten without a heal age: the bucket did not heal, it stopped being a confirmed deficit. Only the number of
      // rows removed is read back, for the re-check's count.
      rowsCleared += (await ledger.MarkHealedBatchWithAgesAsync(undecidedKeys, cancellationToken).ConfigureAwait(false)).Count;
      metrics?.UnplacedBuckets.Add(undecidedKeys.Count,
        new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName));
      LogUnplacedBuckets(logger, undecidedKeys.Count, message.OriginServiceName);
    }
    if (healedKeys.Count > 0) {
      // The heal's own delete reports each bucket's first-sighting age — per-stream
      // time-to-reconcile, measured by destroying the row that carried the clock.
      var healAges = await ledger.MarkHealedBatchWithAgesAsync(healedKeys, cancellationToken).ConfigureAwait(false);
      foreach (var age in healAges) {
        metrics?.BucketHealSeconds.Record(age);
      }
      rowsCleared += healAges.Count;
    }
    if (message.Recheck) {
      metrics?.LedgerRowsRecheckCleared.Add(rowsCleared,
        new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName));
    }
    // #1792: with the paced drain dispatching, a ledger row IS a queued repair, so the caps have to
    // bound which deficits get a row. Runs after the heals, so the backlog it reads is current.
    if (options.RepairDrainEnabled && options.RepairMode == IntegrityRepairMode.AutoRepairCapped) {
      divergent = await _admitToRepairDrainAsync(coordinator, options, message, divergent, metrics, cancellationToken)
        .ConfigureAwait(false);
    }
    var observations = new IntegrityReportObservation[divergent.Count];
    for (var i = 0; i < divergent.Count; i++) {
      // #1842: the kind rides the report, because the ledger row cannot derive it (its digest halves are
      // hashes, not counts) and the paced drain claims only the rows a redelivery can repair.
      var (origin, mine, isDeficit, _) = divergent[i];
      // #2030: the size rides too, so a deficit that grew resets the repair ladder and an origin that moved on does not.
      observations[i] = new IntegrityReportObservation(
        new IntegrityRepairLedger.DivergenceKey(message.OriginServiceId, origin.TenantScope, origin.EventType, origin.StreamId),
        origin.DigestLo, origin.DigestHi, mine?.DigestLo ?? 0, mine?.DigestHi ?? 0, isDeficit,
        origin.EventCount - (mine?.EventCount ?? 0));
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
    if (options.RepairDrainEnabled && deficitIndexes.Count > 0) {
      var stampKeys = deficitIndexes.ConvertAll(i => observations[i].Key);
      if (message.ComputedThrough is long stampUntil) {
        await coordinator.IntegrityStampRepairWindowsAsync(
          message.OriginServiceId, stampKeys, message.SinceSequence ?? 0, stampUntil, cancellationToken).ConfigureAwait(false);
      }
      // #2103: the one event each deficit is missing, when the id lanes name it, so the drain asks for that event only.
      await coordinator.IntegrityRecordMissingEventsAsync(
        message.OriginServiceId, stampKeys,
        deficitIndexes.ConvertAll(i => IntegrityDigestMath.SingleMissingEventId(divergent[i].Origin, divergent[i].Mine)),
        cancellationToken).ConfigureAwait(false);
    }
    // #2115: a re-check's answer is a compare, never a repair request.
    var repairEligible = options.RepairMode == IntegrityRepairMode.AutoRepairCapped && repairBudget > 0
      && !options.RepairDrainEnabled && !message.Recheck;
    IReadOnlyList<bool> repairFlags = [];
    if (repairEligible && deficitIndexes.Count > 0) {
      var deficitKeys = deficitIndexes.ConvertAll(i => observations[i].Key);
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
        new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName),
        new KeyValuePair<string, object?>("event_type", origin.EventType),
        new KeyValuePair<string, object?>("reason", reason));
      var shouldReport = i < reportFlags.Count && reportFlags[i];
      var autoRepair = deficitFlagByIndex.TryGetValue(i, out var granted) && granted;
      if (autoRepair) {
        metrics?.RepairsRequested.Add(1,
          new KeyValuePair<string, object?>("source", "audit"),
          new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName));
        if (!repairBatches.TryGetValue((origin.TenantScope, origin.EventType), out var streams)) {
          repairBatches[(origin.TenantScope, origin.EventType)] = streams = [];
        }
        streams.Add((origin.StreamId, IntegrityDigestMath.SingleMissingEventId(origin, mine)));
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
        tally = new DivergenceTally { SampleStreamId = origin.StreamId };
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
        ReportStreamId = TrackedGuid.New().Value,
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
    // #2103: a stream whose one missing event is named asks for that event; an id list narrows a request to its ids,
    // so those streams go in their own requests (at most MAX_NAMED_EVENTS_PER_REQUEST each) and the rest ask whole.
    foreach (var ((tenantScope, eventType), streams) in repairBatches) {
      foreach (var page in streams.Where(s => s.Missing is not null).Chunk(MAX_NAMED_EVENTS_PER_REQUEST)) {
        await _sendRepairRequestAsync(services, options, message, tenantScope, eventType,
          [.. page.Select(p => p.Stream)], [.. page.Select(p => p.Missing!.Value)], cancellationToken).ConfigureAwait(false);
      }
      var whole = streams.Where(s => s.Missing is null).Select(s => s.Stream).ToList();
      if (whole.Count > 0) {
        await _sendRepairRequestAsync(services, options, message, tenantScope, eventType, whole, null, cancellationToken)
          .ConfigureAwait(false);
      }
    }

    // #80-B cursor-following: a non-null resume cursor means this window has MORE stream pages.
    // Without following, a lane wider than one page only ever audited its first
    // MaxDigestsPerManifest streams — the rest was never compared, never repaired, and the seal
    // never certified the window (the type-level seal rule requires a null cursor).
    await _followResumeCursorAsync(services, options, message, types, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// #1792: decides which deficit buckets this comparison may add to the paced repair drain's ledger.
  /// A bucket the ledger already tracks is always admitted (its row is refreshed, the backlog does not
  /// grow). A NEW bucket is admitted while this comparison has added fewer than
  /// <see cref="StreamIntegrityOptions.MaxAutoRepairRequestsPerAudit"/> and the backlog is below
  /// <see cref="StreamIntegrityOptions.MaxRepairDrainBacklog"/>. Everything else is deferred: nothing is
  /// written for it, it stays divergent, and a later audit re-detects it. Non-deficit divergences are
  /// never repaired, so they pass through untouched. Without a backlog reading (an engine that cannot
  /// probe, or a failed probe) the per-comparison cap alone applies, and every key counts as new.
  /// </summary>
  /// <returns>The divergences to record, in manifest order, with the deferred deficits removed.</returns>
  /// <docs>resilience/stream-integrity#repair-caps</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestDrainAdmissionTests.cs</tests>
  private async Task<List<(StreamDigest Origin, StreamDigest? Mine, bool IsDeficit, string Reason)>> _admitToRepairDrainAsync(
      IWorkCoordinator coordinator, StreamIntegrityOptions options, IntegrityManifest message,
      List<(StreamDigest Origin, StreamDigest? Mine, bool IsDeficit, string Reason)> divergent,
      Whizbang.Core.Observability.StreamIntegrityMetrics? metrics, CancellationToken cancellationToken) {
    var deficitKeys = divergent
      .Where(e => e.IsDeficit)
      .Select(e => new IntegrityRepairLedger.DivergenceKey(
        message.OriginServiceId, e.Origin.TenantScope, e.Origin.EventType, e.Origin.StreamId))
      .ToList();
    if (deficitKeys.Count == 0) {
      return divergent;
    }

    var backlogCap = options.MaxRepairDrainBacklog;
    var probe = await coordinator.IntegrityProbeRepairBacklogAsync(
      message.OriginServiceId, deficitKeys, backlogCap, cancellationToken).ConfigureAwait(false);
    if (probe is not null && probe.Tracked.Count != deficitKeys.Count) {
      probe = null;   // an answer that does not line up with the keys cannot be trusted per key.
    }
    var headroom = probe is null ? long.MaxValue : Math.Max(0L, backlogCap - probe.Backlog);
    var perComparison = Math.Max(0, options.MaxAutoRepairRequestsPerAudit);

    var (admitted, byCap, byBacklog) = _partitionForDrain(divergent, probe?.Tracked, headroom, perComparison);
    // Deferring the repair does not make the divergence any less detected.
    foreach (var (origin, _, _, reason) in divergent.Except(admitted)) {
      metrics?.DivergencesDetected.Add(1,
        new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName),
        new KeyValuePair<string, object?>("event_type", origin.EventType),
        new KeyValuePair<string, object?>("reason", reason));
    }

    if (byCap + byBacklog > 0) {
      LogRepairAdmissionDeferred(logger, byCap + byBacklog, message.OriginServiceName, byCap, perComparison, byBacklog,
        probe is null ? "unknown" : $"{probe.Backlog} of {backlogCap}");
      metrics?.RepairsDeferred.Add(byCap,
        new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName),
        new KeyValuePair<string, object?>(IntegrityManifestTags.DEFERRAL_REASON, "per_audit_cap"));
      metrics?.RepairsDeferred.Add(byBacklog,
        new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName),
        new KeyValuePair<string, object?>(IntegrityManifestTags.DEFERRAL_REASON, "backlog_cap"));
    }
    return admitted;
  }

  /// <summary>
  /// The admission rule itself, kept free of I/O: walks the divergences in manifest order and keeps
  /// every non-deficit, every tracked deficit (<paramref name="tracked"/> answers deficit i; null =
  /// none known), and new deficits while fewer than <paramref name="perComparison"/> have been kept
  /// and fewer than <paramref name="headroom"/>. Each deferral is attributed to the backlog bound when
  /// the headroom is spent, else to the per-comparison cap.
  /// </summary>
  private static (List<(StreamDigest Origin, StreamDigest? Mine, bool IsDeficit, string Reason)> Admitted, int ByCap, int ByBacklog) _partitionForDrain(
      List<(StreamDigest Origin, StreamDigest? Mine, bool IsDeficit, string Reason)> divergent,
      IReadOnlyList<bool>? tracked, long headroom, int perComparison) {
    var admitted = new List<(StreamDigest Origin, StreamDigest? Mine, bool IsDeficit, string Reason)>(divergent.Count);
    var admittedNew = 0;
    var byCap = 0;
    var byBacklog = 0;
    var deficitIndex = 0;
    foreach (var entry in divergent) {
      var keep = true;
      if (entry.IsDeficit) {
        keep = tracked?[deficitIndex] == true;
        deficitIndex++;
      }
      if (!keep && admittedNew < headroom && admittedNew < perComparison) {
        admittedNew++;
        keep = true;
      }
      if (keep) {
        admitted.Add(entry);
      } else if (admittedNew >= headroom) {
        byBacklog++;
      } else {
        byCap++;
      }
    }
    return (admitted, byCap, byBacklog);
  }

  /// <summary>
  /// Per-(origin, window) pages followed in the current audit burst. In-memory and per-process on
  /// purpose: the bound protects THIS replica's compare budget, a restart merely re-follows a few
  /// pages, and carrying a page counter on the wire would hand old peers a field they never echo —
  /// an unbounded chain, the exact failure the bound exists to stop. Entries expire by
  /// timestamp (a fresh audit round restarts the budget), complete windows remove theirs, and a drill-down removes its
  /// window's (#2211: a new walk gets a fresh budget). Where a walk stopped at the budget is kept in the store instead
  /// (IWorkCoordinator.IntegritySaveDrillCursorAsync), so the next audit resumes there on any replica.
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
    var (Pages, _) = _pagesFollowed.TryGetValue(key, out var seen) && now - seen.Last < burstWindow
      ? seen
      : (Pages: 0, Last: now);
    if (Pages >= Math.Max(0, options.MaxManifestPagesPerAudit)) {
      LogCursorFollowCapped(logger, message.OriginServiceName, Pages);
      services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>()?.ManifestPagesCapped.Add(1,
        new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName));
      // #2211: the next drill-down of this window resumes here. Dropping the cursor restarted every audit's walk at the
      // window's first stream, so a window wider than the budget had the same leading streams compared and repaired at
      // every audit and the streams after them never. A re-check walks the window of its own rows and keeps none.
      if (!message.Recheck) {
        await services.GetRequiredService<IWorkCoordinator>().IntegritySaveDrillCursorAsync(
          message.OriginServiceId, message.SinceSequence ?? 0, cursor, now, cancellationToken).ConfigureAwait(false);
      }
      return;
    }

    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = IntegrityRepairSupport.ResolveRepairTopic(options, services);
    if (transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return;
    }
    // Directed or not at all — the same rule as the repair request and the drill-down.
    var originRequestTopic = services.GetService<IntegrityGapTracker>()?.GetRequestTopic(message.OriginServiceId);
    if (string.IsNullOrEmpty(originRequestTopic)) {
      LogCursorFollowSkippedNoOriginTopic(logger, message.OriginServiceName);
      return;
    }

    _pagesFollowed[key] = (Pages + 1, now);
    services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>()?.ManifestPagesFollowed.Add(1,
      new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName));
    if (_pagesFollowed.Count > 256) {
      _pagesFollowed.Clear();   // windows advance; stale keys are waste, not state.
    }
    var envelope = new MessageEnvelope<RequestIntegrityManifest> {
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
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
        // #2115: the rest of a re-check's window is a re-check too.
        Recheck = message.Recheck,
      },
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestIntegrityManifest), instanceProvider, now)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = message.OriginServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.IntegrityControlDestination.For(originRequestTopic, envelope.MessageId.Value, typeof(RequestIntegrityManifest), options), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
    LogCursorFollowed(logger, Pages + 1, message.OriginServiceName);
  }

  /// <summary>
  /// The type-level answer: compared (and sealed, drilled into or bulk-repaired) by <see cref="_compareTypeLevelAsync"/>,
  /// then, when the origin capped the window (#2107), the audit walks on to the next window from where this one stopped,
  /// verified or not, until <see cref="StreamIntegrityOptions.MaxAuditWindowsPerAudit"/> windows have been asked for. A
  /// window that did not verify is not sealed, so the seal holds at the first such window and the next audit starts again
  /// from there; the walk only makes sure the history behind it is still compared in this audit. Continued once per
  /// answer: a multi-chunk answer arrives as several messages, and continuing from each would ask for the next window
  /// once per chunk.
  /// </summary>
  private async Task _handleTypeLevelAsync(
      IServiceProvider services, StreamIntegrityOptions options, IntegrityManifest message,
      List<string> types, TimeSpan settle, CancellationToken cancellationToken) {
    var compared = await _compareTypeLevelAsync(services, options, message, types, settle, cancellationToken).ConfigureAwait(false);
    if (!compared || !message.WindowCapped || message.ChunkCount != 1 || message.ComputedThrough is not long through) {
      return;
    }
    var window = Math.Max(1, message.WindowNumber);   // 0: the audit's first window, from an asker that never numbered it
    if (window >= options.MaxAuditWindowsPerAudit) {
      LogAuditWindowBudgetSpent(logger, message.OriginServiceName, window, through);
      return;
    }
    await _continueAfterCappedWindowAsync(services, options, message, through, window + 1, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// A1c: the type-level half of the hierarchical exchange. Compares the origin's per-(tenant,
  /// type) roll-ups against this consumer's; matching roll-ups prove EVERY stream bucket of the
  /// type complete — one comparison instead of thousands. Mismatched types escalate (capped) to a
  /// DIRECTED stream-level manifest request; reports only ever come from the stream-level compare.
  /// </summary>
  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "The type-level compare matches each type's roll-up, applies the settle window, sorts the mismatches into bulk-backfill candidates and drill-down requests, applies the repair grant cap over the candidates, and addresses each drill-down to its origin's request topic. The stages share the mismatch set they narrow.")]
  private async Task<bool> _compareTypeLevelAsync(
      IServiceProvider services, StreamIntegrityOptions options, IntegrityManifest message,
      List<string> types, TimeSpan settle, CancellationToken cancellationToken) {
    var coordinator = services.GetRequiredService<IWorkCoordinator>();
    var windowed = message.ComputedThrough is not null;
    IReadOnlyList<StreamDigest> local;
    if (message.ComputedThrough is long windowEnd) {
      // #80-B/C: the local fold covers the manifest's EXACT window — anything else compares
      // incomparable ranges and alarms on phantoms. A quiet answer (#2107) names no type, so its window is checked
      // against everything this service consumes there.
      var windowedLocal = await coordinator.ComputeTypeDigestsWindowedAsync(
        message.OriginServiceId, message.Digests.Count == 0 ? ConsumedMessageTypes.EventTypeNames(services) : types,
        message.SinceSequence ?? 0, windowEnd, settle,
        cancellationToken).ConfigureAwait(false);
      if (windowedLocal is null) {
        LogWindowedCompareUnsupported(logger, message.OriginServiceName);
        return false;
      }
      local = windowedLocal.Digests;
    } else {
      local = message.Recomputed
        ? await coordinator.ComputeTypeDigestsAsync(message.OriginServiceId, types, settle, cancellationToken).ConfigureAwait(false)
        : await _typeDigestsWithFallbackAsync(coordinator, message.OriginServiceId, types, settle, cancellationToken).ConfigureAwait(false);
    }
    var localByBucket = local.ToDictionary(d => (d.TenantScope, d.EventType, d.StreamId));
    // #2102: a windowed type fold leaves out the events held without the origin's sequence; place them by identity.
    var heldByBucket = windowed
      ? await _unsequencedByBucketAsync(coordinator, message, localByBucket, settle, cancellationToken).ConfigureAwait(false)
      : [];

    var mismatched = new List<string>();
    var matched = new List<StreamDigest>();
    var bulkCandidates = new List<(StreamDigest Origin, StreamDigest? Mine, long Deficit)>();
    foreach (var origin in message.Digests) {
      var bucket = (origin.TenantScope, origin.EventType, origin.StreamId);
      localByBucket.TryGetValue(bucket, out var placed);
      heldByBucket.TryGetValue(bucket, out var sequenceless);
      var placement = IntegrityDigestMath.PlaceHeld(origin, placed, sequenceless);
      if (placement.Verdict == HeldWindowVerdict.Matches) {
        matched.Add(origin);
        continue;
      }
      var mine = placement.Held;
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
    await _healProvenTypeLanesAsync(services, coordinator, message, matched, cancellationToken).ConfigureAwait(false);
    if (mismatched.Count == 0) {
      // #80-C seal advance — only when the whole window PROVABLY passed: every bucket matched,
      // the answer was one complete chunk, and no resume cursor was pending. Chunks carry no
      // assembly protocol, so a multi-chunk window can never be certified from one chunk — it
      // still compared and repaired above, it just does not move the seal.
      // #2107: a quiet answer verifies its window only when this service holds nothing there either; and the window must
      // start at or below the seal, or sealing it would certify the history beneath it unverified (a seal reset while a
      // continuation was in flight).
      if (windowed && message.ChunkCount == 1 && message.ResumeAfterStreamId is null
          && message.ComputedThrough is long through
          && (message.Digests.Count > 0 || local.Count == 0)
          && (message.SinceSequence ?? 0) <= await coordinator.GetIntegritySealAsync(message.OriginServiceId, cancellationToken).ConfigureAwait(false)) {
        await coordinator.AdvanceIntegritySealAsync(message.OriginServiceId, through, cancellationToken)
          .ConfigureAwait(false);
        LogSealAdvanced(logger, message.OriginServiceName, through);
      }
      return true;   // every type roll-up matches: the whole subscribed surface is provably complete.
    }

    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = IntegrityRepairSupport.ResolveRepairTopic(options, services);
    if (transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return true;   // no drill-down infrastructure: the mismatch re-audits next cycle.
    }
    var sender = new ControlPlaneSender(transport, serializer, instanceProvider, requester, topic);

    // Bulk-deficit escalation: a windowed (tenant, type) deficit at or past the threshold skips
    // the stream drill-down entirely — one state-only, range-bounded redelivery of the whole
    // type's window replaces days of 500-stream pages dripping 25 repairs per cycle. The
    // synthetic empty-stream ledger key gives the bulk ask the same attempt backoff every
    // stream-scoped repair gets, so audit cycles never re-ship a window already in flight.
    var bulkEscalated = new HashSet<string>(StringComparer.Ordinal);
    if (bulkCandidates.Count > 0 && options.RepairMode == IntegrityRepairMode.AutoRepairCapped) {
      var ledger = IntegrityRepairSupport.ResolveLedger(services);
      var now = DateTimeOffset.UtcNow;
      var cooldown = TimeSpan.FromMinutes(options.DivergenceReportCooldownMinutes);
      var backoff = TimeSpan.FromSeconds(options.RepairRequestBackoffSeconds);
      var keys = bulkCandidates
        .ConvertAll(c => new IntegrityRepairLedger.DivergenceKey(message.OriginServiceId, c.Origin.TenantScope, c.Origin.EventType, Guid.Empty))
;
      // Report before repair: the durable ledger only grants repairs for KNOWN divergences, and
      // the report row is the operator-facing record of the type-level deficit itself.
      var observations = new IntegrityReportObservation[bulkCandidates.Count];
      for (var i = 0; i < bulkCandidates.Count; i++) {
        var (origin, mine, deficit) = bulkCandidates[i];
        // Every bulk candidate is a deficit by construction (the origin holds more than this store).
        observations[i] = new IntegrityReportObservation(
          keys[i], origin.DigestLo, origin.DigestHi, mine?.DigestLo ?? 0, mine?.DigestHi ?? 0, IsDeficit: true, deficit);
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
          new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName));
        var sent = await _sendBulkBackfillRequestAsync(services, options, sender, message, origin.TenantScope, origin.EventType, cancellationToken)
          .ConfigureAwait(false);
        if (sent) {
          // #2034: the bulk request answers the type lane and every per-stream drain row of the type inside its window,
          // so those wait for it instead of re-shipping the same window beside it.
          _ = await ledger.MarkAnswerPendingAsync(
            [keys[i]], message.SinceSequence ?? 0, message.ComputedThrough,
            now, TimeSpan.FromMinutes(options.RepairAnswerTimeoutMinutes), cancellationToken).ConfigureAwait(false);
        }
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
      return true;
    }
    var drilledAt = DateTimeOffset.UtcNow;
    foreach (var t in drillDown) {
      _lastDrilled[(message.OriginServiceId, t)] = drilledAt;
    }
    // Directed or not at all — the same rule as the repair request (see
    // _sendRepairRequestAsync): without the origin's carried address the drill-down waits for a
    // checkpoint instead of broadcasting off the requester's own topic.
    var originRequestTopic = services.GetService<IntegrityGapTracker>()?.GetRequestTopic(message.OriginServiceId);
    if (string.IsNullOrEmpty(originRequestTopic)) {
      LogDrillDownSkippedNoOriginTopic(logger, message.OriginServiceName, drillDown.Count);
      return true;
    }
    var since = message.SinceSequence ?? 0;
    Guid? resumeAfter = null;
    if (message.ComputedThrough is long walkUntil) {
      // #2211: a drill-down starts a new walk of its window, with a fresh page budget, from where the last walk the
      // budget stopped left off (the cursor is taken, so it is used once).
      _pagesFollowed.TryRemove((message.OriginServiceId, since, walkUntil), out _);
      resumeAfter = await coordinator.IntegrityTakeDrillCursorAsync(message.OriginServiceId, since, cancellationToken)
        .ConfigureAwait(false);
    }
    var envelope = new MessageEnvelope<RequestIntegrityManifest> {
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
      Payload = new RequestIntegrityManifest {
        RequesterService = requester,
        Topic = topic,
        EventTypes = drillDown,
        Level = ManifestLevel.Streams,
        UseRecompute = message.Recomputed,
        // #80-C: the drill-down inherits the window that disagreed — the stream-level exchange
        // (and the repairs it spawns) stay bounded to that range instead of re-shipping history.
        Windowed = windowed,
        SinceSequence = since,
        UntilSequence = message.ComputedThrough,
        MaxDigests = windowed ? options.MaxDigestsPerManifest : null,
        ResumeAfterStreamId = resumeAfter,
      },
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestIntegrityManifest), instanceProvider, DateTimeOffset.UtcNow)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = message.OriginServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.IntegrityControlDestination.For(originRequestTopic, envelope.MessageId.Value, typeof(RequestIntegrityManifest), options), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
    services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>()?.DrillDownsRequested.Add(1,
      new KeyValuePair<string, object?>(IntegrityManifestTags.ORIGIN, message.OriginServiceName));
    LogDrillDown(logger, drillDown.Count, mismatched.Count, message.OriginServiceName);
    return true;
  }

  /// <summary>
  /// #2207: heals the type-lane ledger row (the empty stream) of every (tenant, type) this answer proves complete, so a
  /// deficit recorded on the type lane, by a bulk backfill or a checkpoint gap, clears once the type compares equal
  /// instead of waiting for a checkpoint window heal that a type with no new traffic never gets. The same proof covers
  /// every stream of the type, so every per-stream row of that (origin, tenant, type) is a phantom and is forgotten in
  /// the same step (phantom heal); a type with a stream genuinely missing has a roll-up that disagrees and never reaches
  /// here.
  /// </summary>
  /// <remarks>
  /// A match is proof only when both sides were counted over the whole history of the type: a full-history answer
  /// (no window), or a window that starts at or below the seal (everything beneath it was verified) and reached the
  /// origin's settled head (the origin did not cap it). A window above the seal or a capped one proves only itself, and
  /// the recorded deficit may lie outside it, so its matches heal nothing. Only a <see cref="HeldWindowVerdict.Matches"/>
  /// verdict reaches here: an undecided type (events held without the origin's sequence that fill the count but not the
  /// fold) is not proven. Each heal records its age on the heal histogram, as every other heal does.
  /// </remarks>
  /// <docs>resilience/stream-integrity#a-matching-type-heals-its-type-lane</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.TypeLaneHeal.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.PhantomHeal.cs</tests>
  private static async Task _healProvenTypeLanesAsync(
      IServiceProvider services, IWorkCoordinator coordinator, IntegrityManifest message, List<StreamDigest> matched,
      CancellationToken cancellationToken) {
    if (matched.Count == 0) {
      return;
    }
    if (message.ComputedThrough is not null
        && (message.WindowCapped
            || (message.SinceSequence ?? 0) > await coordinator.GetIntegritySealAsync(message.OriginServiceId, cancellationToken).ConfigureAwait(false))) {
      return;
    }
    var ledger = IntegrityRepairSupport.ResolveLedger(services);
    // The same proof covers every stream of the type, so its per-stream rows are phantoms and go with the type lane.
    var ages = await ledger.MarkTypesHealedWithAgesAsync(
      matched.ConvertAll(d => new IntegrityRepairLedger.DivergenceKey(message.OriginServiceId, d.TenantScope, d.EventType, Guid.Empty)),
      cancellationToken).ConfigureAwait(false);
    var metrics = services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
    foreach (var age in ages) {
      metrics?.BucketHealSeconds.Record(age);
    }
  }

  /// <summary>
  /// #2107: asks the origin for window <paramref name="window"/> of this audit, from where the capped one stopped, so the
  /// rest of the settled history is compared in bounded slices now rather than one slice per audit interval. Directed or
  /// not at all, as every integrity request: without the origin's address the next scheduled audit continues from the
  /// seal.
  /// </summary>
  private async Task _continueAfterCappedWindowAsync(
      IServiceProvider services, StreamIntegrityOptions options, IntegrityManifest message, long through, int window,
      CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = IntegrityRepairSupport.ResolveRepairTopic(options, services);
    var originRequestTopic = services.GetService<IntegrityGapTracker>()?.GetRequestTopic(message.OriginServiceId);
    if (transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)
        || string.IsNullOrEmpty(originRequestTopic)) {
      LogCappedWindowNotContinued(logger, message.OriginServiceName, through);
      return;
    }
    var absorbed = ConsumedMessageTypes.AbsorbedNamespaces(services);
    var envelope = new MessageEnvelope<RequestIntegrityManifest> {
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
      Payload = new RequestIntegrityManifest {
        RequesterService = requester,
        Topic = topic,
        // The same ask the scheduled audit makes, from the new seal: the whole consumed surface, type level.
        EventTypes = ConsumedMessageTypes.EventTypeNames(services),
        EventNamespaces = absorbed.Count > 0 ? absorbed : null,
        Level = ManifestLevel.Types,
        Windowed = true,
        SinceSequence = through,
        WindowNumber = window,
      },
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestIntegrityManifest), instanceProvider, DateTimeOffset.UtcNow)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = message.OriginServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.IntegrityControlDestination.For(originRequestTopic, envelope.MessageId.Value, typeof(RequestIntegrityManifest), options), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
    LogCappedWindowContinued(logger, message.OriginServiceName, through, window);
  }

  /// <summary>Type-level table reads, falling back to the recompute when the table has no rows: either
  /// nothing was received (recompute is equally empty, cheap) or the provider lacks the digest
  /// table (the DIM default returns empty; the recompute is the honest source).</summary>
  /// <remarks>Type level only. The stream-level compare reads its table and falls back to the
  /// chunk-bounded fold itself (#453), because a whole-store stream recompute is the shape that
  /// memory-killed consumers.</remarks>
  private static async Task<IReadOnlyList<StreamDigest>> _typeDigestsWithFallbackAsync(
      IWorkCoordinator coordinator, Guid originServiceId, List<string> types, TimeSpan settle,
      CancellationToken cancellationToken) {
    var table = await coordinator.GetTypeDigestsAsync(originServiceId, types, cancellationToken).ConfigureAwait(false);
    if (table.Count > 0) {
      return table;
    }
    return await coordinator.ComputeTypeDigestsAsync(originServiceId, types, settle, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The bulk half of #80-C's range-bounded backfill: ONE state-only redelivery of a whole
  /// (tenant, type) window — no stream list, because the deficit spans more streams than the
  /// per-stream path can drip through in any reasonable number of cycles. State-only is
  /// load-bearing: backfilled history builds state and never re-fires trigger receptors.
  /// </summary>
  /// <summary>
  /// The control-plane essentials an outbound integrity request publishes with, taken as a unit so
  /// the "is the infrastructure here at all?" question is answered once, by the caller that already
  /// had to ask it, instead of being re-asked of the same provider and re-answered the same way.
  /// </summary>
  private sealed record ControlPlaneSender(
    ITransport Transport,
    IEnvelopeSerializer Serializer,
    IServiceInstanceProvider? InstanceProvider,
    string Requester,
    string Topic);

  private async Task<bool> _sendBulkBackfillRequestAsync(
      IServiceProvider services, StreamIntegrityOptions options, ControlPlaneSender sender,
      IntegrityManifest manifest, string? tenantScope, string eventType,
      CancellationToken cancellationToken) {
    var (transport, serializer, instanceProvider, requester, topic) = sender;
    var originRequestTopic = services.GetService<IntegrityGapTracker>()?.GetRequestTopic(manifest.OriginServiceId);
    if (string.IsNullOrEmpty(originRequestTopic)) {
      LogRepairSkippedNoOriginTopic(logger, manifest.OriginServiceName, eventType, 0);
      return false;
    }

    var (fromSequence, toSequence) = IntegrityRepairSupport.RedeliveryWindow(manifest);
    var envelope = new MessageEnvelope<RequestRedeliveryCommand> {
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
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
        FromCommitSequence = fromSequence,
        ToCommitSequence = toSequence,
        StateOnly = true,
      },
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestRedeliveryCommand), instanceProvider, DateTimeOffset.UtcNow)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = manifest.OriginServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.IntegrityControlDestination.For(originRequestTopic, envelope.MessageId.Value, typeof(RequestRedeliveryCommand), options), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
    return true;
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "One repair request: where it is answered (services, options, manifest), what it asks for (tenant, type, streams, named events) and its cancellation. A request object would only restate these fields.")]
  private async Task _sendRepairRequestAsync(
      IServiceProvider services, StreamIntegrityOptions options,
      IntegrityManifest manifest, string? tenantScope, string eventType, List<Guid> streamIds, List<Guid>? eventIds,
      CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = IntegrityRepairSupport.ResolveRepairTopic(options, services);
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

    var (fromSequence, toSequence) = IntegrityRepairSupport.RedeliveryWindow(manifest);
    var envelope = new MessageEnvelope<RequestRedeliveryCommand> {
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
      Payload = new RequestRedeliveryCommand {
        TenantScope = tenantScope,
        EventTypes = [eventType],
        StreamIds = streamIds,
        EventIds = eventIds,
        RequesterService = requester,
        Topic = topic,
        // #80-C range-bounded backfill: a deficit found comparing a WINDOW is a deficit in that
        // window — the origin re-ships the slice, not the streams' whole history. [since, until)
        // maps to the command's exclusive-floor / inclusive-ceiling pair; legacy (unwindowed)
        // manifests leave both null, the pre-existing whole-history semantics.
        FromCommitSequence = fromSequence,
        ToCommitSequence = toSequence,
      },
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestRedeliveryCommand), instanceProvider, DateTimeOffset.UtcNow)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = manifest.OriginServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.IntegrityControlDestination.For(originRequestTopic, streamIds[0], typeof(RequestRedeliveryCommand), options), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Running per-(tenant, type) totals so divergence is logged once per bucket instead of once per
  /// stream. Hundreds of near-identical lines cost real work on the thread that owes the liveness
  /// probe an answer, and bury the signal an operator is actually looking for.
  /// </summary>
  private sealed class DivergenceTally {
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
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "LoggerMessage source-generated method: the parameter list mirrors the structured log template's placeholders and cannot be grouped without losing structured-logging semantics.")]
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

  [LoggerMessage(EventId = 81, Level = LogLevel.Information,
    Message = "Integrity audit vs origin '{OriginServiceName}': the origin capped the window at sequence {Through}; " +
              "asked for window {Window} of this audit from there")]
  static partial void LogCappedWindowContinued(ILogger logger, string originServiceName, long through, int window);

  [LoggerMessage(EventId = 82, Level = LogLevel.Debug,
    Message = "Integrity audit vs origin '{OriginServiceName}': the origin capped the window at sequence {Through}, " +
              "but its request address is not known yet; the next scheduled audit continues from the seal")]
  static partial void LogCappedWindowNotContinued(ILogger logger, string originServiceName, long through);

  [LoggerMessage(EventId = 83, Level = LogLevel.Information,
    Message = "Integrity audit vs origin '{OriginServiceName}': walked {Windows} window(s) this audit, through sequence " +
              "{Through}, the most MaxAuditWindowsPerAudit allows; the next audit continues from the seal")]
  static partial void LogAuditWindowBudgetSpent(ILogger logger, string originServiceName, int windows, long through);

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
              "the next audit's drill-down of the window resumes where this walk stopped")]
  static partial void LogCursorFollowCapped(ILogger logger, string originServiceName, int pagesFollowed);

  [LoggerMessage(EventId = 65, Level = LogLevel.Debug,
    Message = "AUDIT cursor-follow to '{OriginServiceName}' skipped — no origin request topic learned yet")]
  static partial void LogCursorFollowSkippedNoOriginTopic(ILogger logger, string originServiceName);

  [LoggerMessage(EventId = 67, Level = LogLevel.Warning,
    Message = "AUDIT Deferred {Deferred} new deficit bucket(s) from '{OriginServiceName}' instead of adding them to the " +
              "paced repair drain: {ByCap} past the per-audit cap of {PerAuditCap}, {ByBacklog} past the backlog bound; " +
              "drain backlog {Backlog}. They stay divergent and a later audit re-detects them once the drain has made room.")]
  static partial void LogRepairAdmissionDeferred(ILogger logger, int deferred, string originServiceName, int byCap,
    int perAuditCap, int byBacklog, string backlog);

  [LoggerMessage(EventId = 66, Level = LogLevel.Warning,
    Message = "AUDIT bulk backfill requested: {EventType} (tenant {TenantScope}) — {Deficit} event(s) missing " +
              "vs origin '{OriginServiceName}' in the audited window; ONE state-only range-bounded redelivery replaces the per-stream drip")]
  static partial void LogBulkBackfillRequested(ILogger logger, string eventType, string? tenantScope, long deficit, string originServiceName);

  [LoggerMessage(EventId = 68, Level = LogLevel.Information,
    Message = "AUDIT {Count} windowed stream bucket(s) from '{OriginServiceName}' left undecided: this service holds some " +
              "of their events without the origin's commit sequence, so the window cannot confirm a deficit. None is " +
              "recorded or repaired; the full-history comparison decides those streams.")]
  static partial void LogUnplacedBuckets(ILogger logger, int count, string originServiceName);

  /// <summary>
  /// #2102: the folds of the events this service holds from the manifest's origin without the origin's commit
  /// sequence, keyed like the local fold, for the buckets whose placed fold disagrees with the origin's. Nothing is read
  /// when every bucket agrees; otherwise one read, bounded by the disagreeing buckets' streams (stream level) or the
  /// origin's sequence-less rows of the disagreeing types (type level).
  /// </summary>
  private static async Task<Dictionary<(string? TenantScope, string EventType, Guid StreamId), StreamDigest>> _unsequencedByBucketAsync(
      IWorkCoordinator coordinator, IntegrityManifest message,
      Dictionary<(string? TenantScope, string EventType, Guid StreamId), StreamDigest> localByBucket,
      TimeSpan settle, CancellationToken cancellationToken) {
    var disagreeing = message.Digests
      .Where(o => !localByBucket.TryGetValue((o.TenantScope, o.EventType, o.StreamId), out var mine)
        || mine.DigestLo != o.DigestLo || mine.DigestHi != o.DigestHi)
      .ToList();
    if (disagreeing.Count == 0) {
      return [];
    }
    var types = disagreeing.Select(d => d.EventType).Distinct(StringComparer.Ordinal).ToList();
    IReadOnlyList<Guid>? streams = message.Level == ManifestLevel.Types
      ? null
      : [.. disagreeing.Select(d => d.StreamId).Distinct()];
    var held = await coordinator.ComputeUnsequencedReceivedDigestsAsync(
      message.OriginServiceId, types, streams, settle, cancellationToken).ConfigureAwait(false);
    return held.ToDictionary(d => (d.TenantScope, d.EventType, d.StreamId));
  }
}

/// <summary>
/// Registers the Phase A manifest receptors with <see cref="IReceptorRegistry"/> at startup — the
/// same runtime-registration rationale as every framework receptor in this assembly.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6672:Generic logger injection should match enclosing type", Justification = "The registrar never logs. It receives the logger for the receptor it constructs and hands it straight over, so the category names the type that actually writes the entries.")]
internal sealed class IntegrityManifestReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrityManifestRequestReceptor> requestLogger,
    ILogger<IntegrityManifestReceptor> manifestLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    // INullDefault, not null: every framework dependency resolves now, so "no registry" arrives as
    // the turnkey default rather than as an absent service. Registering into it throws by design,
    // which would take the whole host down on a service that simply declares no receptors.
    if (registry is null or INullDefault) {
      return Task.CompletedTask;
    }
    // Deferred at every stage: answering and comparing manifests run on the maintainer's integrity delivery drain (#2001).
    registry.RegisterIntegrityReceptor(new IntegrityManifestRequestReceptor(scopeFactory, requestLogger), services, scopeFactory);
    registry.RegisterIntegrityReceptor(new IntegrityManifestReceptor(scopeFactory, manifestLogger), services, scopeFactory);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
