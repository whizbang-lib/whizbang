// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// Stream-integrity Phases A + L: the scheduled deep audit (default daily, ON by default).
/// Each cycle runs two halves:
/// <list type="bullet">
/// <item><b>Local (L)</b> — perspective coverage gaps (settled history a registered perspective
/// never folded: no cursor, no pending work) → <see cref="PerspectiveCoverageGapDetected"/>
/// reports; at <see cref="IntegrityRepairMode.AutoRepairCapped"/> a capped LOCAL
/// <see cref="RebuildPerspectiveCommand"/> per gap.</item>
/// <item><b>Cross-service (A)</b> — for every origin this consumer knows (the checkpoint
/// tracker's origin set), a DIRECTED <see cref="RequestIntegrityManifest"/> asking for its
/// identity digests of the event types this consumer consumes (#2058: its consumer registry, plus the
/// namespaces it absorbs; never every type it knows). The manifests come back targeted;
/// comparison happens in the manifest receptor.</item>
/// </list>
/// Runs on the instance holding the maintainer duty, and only once the housekeeping idle gate admits integrity work
/// (forced through after <c>Whizbang:Housekeeping:MaxDeferral</c>): the audit's recomputes and requests compete with
/// live traffic for the same store (#2001). A cycle it could not run is retried on the startup window.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/IntegrityAuditWorkerTests.cs</tests>
public sealed partial class IntegrityAuditWorker(
  IServiceScopeFactory scopeFactory,
  IOptions<StreamIntegrityOptions> options,
  ILogger<IntegrityAuditWorker> logger) : BackgroundService, IIntegritySweepRunner {
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<IntegrityAuditWorker> _logger = ArgumentGuard.NotNull(logger);
  private int _cycleCount;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.AuditEnabled) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { /* stopping is the normal way out of this wait */ }
      return;
    }
    LogStarted(_logger, _options.AuditIntervalMinutes);

    var firstCycle = true;
    // A cycle that reached no origins has not audited across services; the next delay re-arms the
    // startup window rather than the steady interval.
    var askedNobody = false;
    while (!stoppingToken.IsCancellationRequested) {
      // Startup-first by default: the first audit fires after a jittered startup window so
      // historical divergence heals minutes after a deploy — the jitter de-synchronizes a fleet
      // rollout and A1c's type-level exchange keeps each audit at O(types) wire cost. Opting out
      // (AuditOnStartup=false) restores interval-first scheduling; Phase B covers the fresh
      // window either way.
      var delay = ComputeNextAuditDelay(_options, Random.Shared.NextDouble, askedNobody: firstCycle || askedNobody);
      firstCycle = false;
      try {
        await Task.Delay(delay, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
      try {
        askedNobody = await _runAuditCoreAsync(forceSweep: false, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogError(_logger, ex);
      }
    }
  }

  /// <summary>
  /// The first cycle's delay: a jittered startup window (30s floor + up to
  /// <see cref="StreamIntegrityOptions.StartupAuditMaxJitterSeconds"/> splay) when
  /// <see cref="StreamIntegrityOptions.AuditOnStartup"/> is on; the full interval otherwise.
  /// </summary>
  /// <param name="options">Integrity options.</param>
  /// <param name="jitter">Uniform [0,1) source (injectable for deterministic tests).</param>
  internal static TimeSpan ComputeFirstAuditDelay(StreamIntegrityOptions options, Func<double> jitter) =>
    ComputeNextAuditDelay(options, jitter, askedNobody: true);

  /// <summary>
  /// The delay before the next cycle. The jittered startup window is used for the first cycle and
  /// again after any cycle that had no origins to ask — such a cycle audited nothing across
  /// services, and waiting out the full interval (a day by default) to discover the fleet is
  /// indistinguishable from never discovering it. The jitter is retained on the retry so a fleet
  /// that all asked nobody does not then all retry in unison.
  /// </summary>
  /// <param name="options">Integrity options.</param>
  /// <param name="jitter">Uniform [0,1) source (injectable for deterministic tests).</param>
  /// <param name="askedNobody">Whether the cycle just completed reached no origins at all.</param>
  internal static TimeSpan ComputeNextAuditDelay(
      StreamIntegrityOptions options, Func<double> jitter, bool askedNobody) =>
    options.AuditOnStartup && askedNobody
      ? TimeSpan.FromSeconds(30 + (Math.Max(0, options.StartupAuditMaxJitterSeconds) * jitter()))
      : TimeSpan.FromMinutes(options.AuditIntervalMinutes);

  /// <summary>One audit cycle: the local coverage half, then the cross-service manifest requests.</summary>
  public Task RunAuditOnceAsync(CancellationToken cancellationToken) =>
    _runAuditCoreAsync(forceSweep: false, cancellationToken);

  /// <inheritdoc />
  /// <remarks>#80-D: the scheduled idle-time entry point. The cycle claim still applies, so a
  /// just-swept service's next steady audit is naturally deduped too.</remarks>
  public Task RunSweepOnceAsync(CancellationToken cancellationToken) =>
    _runAuditCoreAsync(forceSweep: true, cancellationToken);

  /// <summary>
  /// One audit cycle. Returns <see langword="true"/> when the cross-service half had origins it
  /// could reach but asked <em>none</em> of them — a cycle that audited nothing and should be
  /// retried on the startup window rather than the full interval. A deployment with no
  /// cross-service infrastructure at all returns <see langword="false"/>: that is a configuration,
  /// not a cold start, and retrying sooner would never help.
  /// </summary>
  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "One audit cycle runs the local half (verification, epoch drift, gap reports, optional rebuild) and the cross-service half (ask each reachable origin), and its return value distinguishes a cycle that asked nobody from a deployment with nobody to ask. Both halves feed that one answer.")]
  private async Task<bool> _runAuditCoreAsync(bool forceSweep, CancellationToken cancellationToken) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    using var priorityScope = Whizbang.Core.Priority.PriorityContext.Enter(Whizbang.Core.Priority.WorkPriority.BACKGROUND);   // system work nobody waits on: everything dispatched here inherits background
    var services = scope.ServiceProvider;
    var coordinator = services.GetService<IWorkCoordinator>();
    var dispatcher = services.GetService<IDispatcher>();
    if (coordinator is null || dispatcher is null) {
      return false;
    }
    // #2001: fleet-wide integrity work runs on the maintainer, and only when the idle gate admits it. A cycle that did
    // not run audited nothing, so it reports so and is retried on the startup window, not a full interval later.
    if (!MaintainerDuty.IsThisInstancesTurn(services)) {
      return true;
    }
    using var admission = await IntegrityAdmission.TryAdmitAsync(
      services, coordinator, nameof(IntegrityAuditWorker), _logger, cancellationToken).ConfigureAwait(false);
    if (!admission.Granted) {
      return true;
    }
    var settle = TimeSpan.FromMinutes(_options.AuditSettleWindowMinutes);
    var metrics = services.GetService<Observability.StreamIntegrityMetrics>();

    // ── one audit per SERVICE per cycle ──────────────────────────────────
    // The same first-instance-wins discipline schema init (advisory lock) and the deep prune
    // (settings CAS) already apply: replicas race a shared watermark, one wins the cycle, the
    // rest skip. Without this every replica runs its own full-store digest recompute — identical
    // results, multiplied load. Half the interval: replicas starting together dedupe, and the
    // next legitimate cycle is never blocked by its own previous claim.
    var claimWindow = TimeSpan.FromMinutes(Math.Max(1, _options.AuditIntervalMinutes) / 2.0);
    if (!await coordinator.TryClaimIntegrityAuditCycleAsync(claimWindow, cancellationToken).ConfigureAwait(false)) {
      LogAuditCycleAlreadyClaimed(_logger, claimWindow.TotalMinutes);
      return false;
    }

    // ── A1c: the full-sweep cadence ──────────────────────────────────────
    // Steady-state cycles run on the incrementally-maintained digest table (O(buckets), with
    // settle-skip on busy buckets). Every Nth cycle is the trust-but-verify sweep: heal our OWN
    // table against a full recompute (non-zero drift = an unaccounted write path — alarm), and
    // exchange recomputed digests end to end so busy buckets get their coverage too.
    // #80-D: once the idle-time cron owns the sweep (the driver registered it on the temporal
    // engine), the counter stands down — otherwise the heaviest work runs on both cadences, and
    // the counter lands it at arbitrary load times. Hosts without the temporal engine never set
    // the state, keeping the every-Nth-cycle counter as the fallback.
    var cronOwnsTheSweep = services.GetService<IntegritySweepScheduleState>()?.CronActive == true;
    var cycle = Interlocked.Increment(ref _cycleCount);
    var sweep = forceSweep
      || (!cronOwnsTheSweep && _options.FullSweepEveryNthAudit > 0 && cycle % _options.FullSweepEveryNthAudit == 0);
    if (sweep) {
      var verification = await coordinator.VerifyDigestTableAsync(settle, cancellationToken).ConfigureAwait(false);
      metrics?.DigestBucketsVerified.Add(verification.BucketsChecked);
      if (verification.TotalDrift > 0) {
        metrics?.DigestDriftHealed.Add(verification.DriftUpdated, new KeyValuePair<string, object?>("kind", "updated"));
        metrics?.DigestDriftHealed.Add(verification.DriftRemoved, new KeyValuePair<string, object?>("kind", "removed"));
        metrics?.DigestDriftHealed.Add(verification.DriftAdded, new KeyValuePair<string, object?>("kind", "added"));
        LogDigestDrift(_logger, verification.TotalDrift, verification.DriftUpdated,
          verification.DriftRemoved, verification.DriftAdded, verification.BucketsChecked);
      } else {
        LogDigestVerified(_logger, verification.BucketsChecked);
      }

      // #80-D: the seal backstop. Manifest answers trust sealed epochs WITHOUT re-verifying (the
      // whole point of the epochs), so this is the ONE place a bad seal gets caught. Bounded per
      // sweep — a very large store finishes across several nightly sweeps.
      var epochVerification = await coordinator.VerifyDigestEpochsAsync(
        settle, _options.MaxEpochVerificationsPerSweep, cancellationToken).ConfigureAwait(false);
      if (epochVerification.EpochsDrifted > 0) {
        metrics?.DigestDriftHealed.Add(epochVerification.EpochsDrifted,
          new KeyValuePair<string, object?>("kind", "epoch-refolded"));
        LogEpochDrift(_logger, epochVerification.EpochsDrifted, epochVerification.EpochsChecked);
      } else if (epochVerification.EpochsChecked > 0) {
        LogEpochsVerified(_logger, epochVerification.EpochsChecked);
      }
    }

    // ── L: local perspective coverage ────────────────────────────────────
    // Both the query and the report loop are bounded by MaxCoverageGapReportsPerAudit — a
    // systematically-uncovered perspective can surface thousands of gaps in one cycle, and an
    // unbounded report loop flooded a live consumer's dispatcher at startup (crashloop). The
    // remainder re-audits next cycle as repairs shrink it.
    var maxGapReports = Math.Max(1, _options.MaxCoverageGapReportsPerAudit);
    var gaps = await coordinator.GetPerspectiveCoverageGapsAsync(settle, maxGapReports, cancellationToken).ConfigureAwait(false);
    if (gaps.Count >= maxGapReports) {
      LogCoverageGapsCapped(_logger, maxGapReports);
    }
    var rebuildBudget = _options.MaxAutoRebuildsPerAudit;
    foreach (var gap in gaps) {
      var autoRebuild = _options.RepairMode == IntegrityRepairMode.AutoRepairCapped && rebuildBudget > 0;
      metrics?.CoverageGapsDetected.Add(1, new KeyValuePair<string, object?>("perspective", gap.PerspectiveName));
      if (autoRebuild) {
        rebuildBudget--;
        metrics?.RebuildsRequested.Add(1, new KeyValuePair<string, object?>("perspective", gap.PerspectiveName));
        await dispatcher.SendAsync(new RebuildPerspectiveCommand(
          PerspectiveNames: [gap.PerspectiveName],
          IncludeStreamIds: [gap.StreamId])).ConfigureAwait(false);
      }
      await dispatcher.PublishAsync(new PerspectiveCoverageGapDetected {
        ReportStreamId = TrackedGuid.New().Value,
        PerspectiveName = gap.PerspectiveName,
        GapStreamId = gap.StreamId,
        EventCount = gap.EventCount,
        AutoRebuildRequested = autoRebuild,
      }).ConfigureAwait(false);
      LogCoverageGap(_logger, gap.PerspectiveName, gap.StreamId, gap.EventCount, autoRebuild);
    }

    // ── the integrity surface: what this service consumes (#2058) ────────
    // The events something here consumes, plus the namespaces it absorbs (stored though nothing consumes them): the
    // receive boundary's keep decision. A type outside it is never stored here, so every bucket of it reads as a deficit
    // whose redelivery the receive boundary discards; it is neither audited nor kept on the repair ledger.
    var consumed = ConsumedMessageTypes.EventTypeNames(services);
    var absorbed = ConsumedMessageTypes.AbsorbedNamespaces(services);
    var forgotten = await coordinator.IntegrityForgetUnconsumedEventTypesAsync(consumed, absorbed, cancellationToken)
      .ConfigureAwait(false);
    if (forgotten > 0) {
      LogForgotUnconsumedLedgerRows(_logger, forgotten);
    }
    // A row recorded under an origin that does not own its type (written before #2032, when a service still reported
    // events it had only received as its own) is never compared again: that origin's answers leave the type out (#2008)
    // and the owner's land under the owner's id. It can neither heal nor clear, so it is forgotten by the same rule the
    // comparison uses to ignore such an origin.
    var ownership = new IntegrityDomainOwnership(
      await coordinator.GetIntegrityDomainClaimsAsync(cancellationToken).ConfigureAwait(false));
    var ownDomains = services.GetService<IOptions<Whizbang.Core.Routing.RoutingOptions>>()?.Value.OwnedDomains
      ?? (IReadOnlyCollection<string>)[];
    // #2185: in the same sweep, a row for a type no service owns goes once the origin that reported it has been silent
    // past the threshold: no owner can repair it, and its origin no longer answers.
    var silenceDays = _options.UnownedTypeOriginSilenceDays;
    var silentBefore = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().AddDays(-silenceDays);
    var forgottenNonOwner = await coordinator.IntegrityForgetNonOwnerLedgerRowsAsync(
      (origin, eventType, lastReportedAt) => ownership.IsNotAuthoritative(eventType, origin, ownDomains)
        || (lastReportedAt < silentBefore && ownership.HasNoOwner(eventType, ownDomains)),
      cancellationToken).ConfigureAwait(false);
    if (forgottenNonOwner > 0) {
      LogForgotNonOwnerLedgerRows(_logger, forgottenNonOwner, silenceDays);
    }
    if (consumed.Count == 0 && absorbed.Count == 0) {
      LogNothingConsumed(_logger);
      return false;   // nothing to audit across services: a configuration, not a cold fleet.
    }

    // ── A: cross-service manifest requests ───────────────────────────────
    var tracker = services.GetService<IntegrityGapTracker>();
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = RepairTopicResolver.Resolve(_options.RepairTopic, services);
    if (tracker is null || transport is null || serializer is null
        || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return false;   // no cross-service infrastructure — the local half already ran.
    }

    // The assembly-qualified WIRE form ("Type, Assembly") — the origin matches these against its
    // event_type/digest columns, which store that form; a CLR-FullName-only list silently
    // matches nothing and every origin answers with silence. Never null: null asks for every type.
    var eventNamespaces = absorbed.Count > 0 ? absorbed : null;
    var requested = 0;
    // #2104: a restarted process asks the origins the store kept, not only those whose checkpoint arrived since.
    await tracker.EnsureHydratedAsync(coordinator, cancellationToken).ConfigureAwait(false);
    foreach (var (originId, originName, originRequestTopic) in tracker.GetOrigins()) {
      if (string.IsNullOrEmpty(originRequestTopic)) {
        // Directed or not at all: without the origin-carried address the only other topic on
        // hand is this consumer's own, and publishing there fans the request out to every
        // service on the shared topic. The origin's next checkpoint teaches the address.
        LogManifestRequestSkippedNoTopic(_logger, originName, originId);
        continue;
      }
      // #80-B/C: steady-state cycles ask WINDOWED from the stored seal — verified history is
      // never re-shipped or re-verified. The sweep deliberately asks FULL: it is trust-but-verify
      // for exactly the state the seals assume is fine; a windowed sweep would be circular trust.
      // An origin that predates the protocol ignores the extra fields and answers unwindowed —
      // the comparator then compares legacy and the seal simply stays put.
      var since = sweep ? 0L : await coordinator.GetIntegritySealAsync(originId, cancellationToken).ConfigureAwait(false);
      var envelope = new MessageEnvelope<RequestIntegrityManifest> {
        Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
        MessageId = new MessageId(TrackedGuid.New()),
        Payload = new RequestIntegrityManifest {
          RequesterService = requester,
          Topic = topic,
          EventTypes = consumed,
          EventNamespaces = eventNamespaces,
          Level = ManifestLevel.Types,
          UseRecompute = sweep,
          Windowed = !sweep,
          SinceSequence = since,
        },
        Hops = [
          Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestIntegrityManifest), instanceProvider, DateTimeOffset.UtcNow)
        ],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
        Target = originName,
      };
      var serialized = serializer.SerializeEnvelope(envelope);
      // Publish the DIRECTED request to the ORIGIN-carried address (a topic the origin actually
      // consumes) — never to the requester's own topics (observed live: requests published
      // there were never received by any origin, only fanned out as noise).
      await transport.PublishAsync(serialized.JsonEnvelope,
        IntegrityControlDestination.For(originRequestTopic, envelope.MessageId.Value, typeof(RequestIntegrityManifest), _options), serialized.EnvelopeType,
        cancellationToken: cancellationToken).ConfigureAwait(false);
      metrics?.ManifestsRequested.Add(1,
        new KeyValuePair<string, object?>("origin", originName),
        new KeyValuePair<string, object?>("sweep", sweep));
      LogManifestRequested(_logger, originName, originId);
      requested++;
    }

    // Nobody to ask. On a cold fleet start the origin set is still empty — it fills only from
    // inbound checkpoints — so this cycle audited nothing across services. Saying so lets the
    // caller retry on the startup window instead of sleeping out the full interval.
    if (requested == 0) {
      LogNoOriginsToAsk(_logger);
      return true;
    }
    return false;
  }

  [LoggerMessage(EventId = 97, Level = LogLevel.Information,
    Message = "Integrity audit forgot {RowCount} repair-ledger row(s) of event types this service does not consume: "
            + "they can never heal, because their redelivery is discarded on receive.")]
  static partial void LogForgotUnconsumedLedgerRows(ILogger logger, int rowCount);

  [LoggerMessage(EventId = 99, Level = LogLevel.Information,
    Message = "Integrity audit forgot {RowCount} repair-ledger row(s) recorded under an origin that does not own the event type, "
            + "or for a type no service owns from an origin silent for over {SilenceDays} day(s): no owner will ever answer them, "
            + "so the rows could never heal or clear.")]
  static partial void LogForgotNonOwnerLedgerRows(ILogger logger, int rowCount, int silenceDays);

  [LoggerMessage(EventId = 98, Level = LogLevel.Debug,
    Message = "Integrity audit asked no origin: this service consumes no events and absorbs no namespace.")]
  static partial void LogNothingConsumed(ILogger logger);

  [LoggerMessage(EventId = 96, Level = LogLevel.Information,
    Message = "Integrity audit reached no origins — none have checkpointed yet. Retrying on the "
            + "startup window rather than the full interval.")]
  static partial void LogNoOriginsToAsk(ILogger logger);

  [LoggerMessage(EventId = 80, Level = LogLevel.Information,
    Message = "Integrity audit started (interval {IntervalMinutes}m)")]
  static partial void LogStarted(ILogger logger, int intervalMinutes);

  [LoggerMessage(EventId = 81, Level = LogLevel.Information,
    Message = "Integrity audit disabled by configuration")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 82, Level = LogLevel.Warning,
    Message = "LOCAL coverage gap: perspective '{PerspectiveName}' never folded {EventCount} settled event(s) on stream {GapStreamId} (autoRebuild={AutoRebuildRequested})")]
  static partial void LogCoverageGap(ILogger logger, string perspectiveName, Guid gapStreamId, int eventCount, bool autoRebuildRequested);

  [LoggerMessage(EventId = 83, Level = LogLevel.Debug,
    Message = "Integrity manifest requested from origin '{OriginServiceName}' ({OriginServiceId})")]
  static partial void LogManifestRequested(ILogger logger, string originServiceName, Guid originServiceId);

  [LoggerMessage(EventId = 84, Level = LogLevel.Error,
    Message = "Integrity audit cycle failed; will retry next interval")]
  static partial void LogError(ILogger logger, Exception exception);

  [LoggerMessage(EventId = 85, Level = LogLevel.Warning,
    Message = "DIGEST DRIFT healed on sweep: {TotalDrift} bucket(s) (updated {DriftUpdated}, removed {DriftRemoved}, " +
              "added {DriftAdded}) of {BucketsChecked} checked — an unaccounted write path touched audited rows")]
  static partial void LogDigestDrift(ILogger logger, int totalDrift, int driftUpdated, int driftRemoved,
    int driftAdded, int bucketsChecked);

  [LoggerMessage(EventId = 86, Level = LogLevel.Information,
    Message = "Digest table verified clean on sweep ({BucketsChecked} settled bucket(s))")]
  static partial void LogDigestVerified(ILogger logger, int bucketsChecked);

  [LoggerMessage(EventId = 87, Level = LogLevel.Warning,
    Message = "Coverage-gap reports reached the per-cycle cap ({MaxGapReports}) — more gaps likely remain; they re-audit next cycle as repairs shrink the set")]
  static partial void LogCoverageGapsCapped(ILogger logger, int maxGapReports);

  [LoggerMessage(EventId = 88, Level = LogLevel.Information,
    Message = "Manifest request to '{OriginServiceName}' ({OriginServiceId}) withheld — no origin-carried " +
              "request address yet; the origin's next checkpoint teaches it")]
  static partial void LogManifestRequestSkippedNoTopic(ILogger logger, string originServiceName, Guid originServiceId);

  [LoggerMessage(EventId = 89, Level = LogLevel.Debug,
    Message = "Audit cycle skipped — a sibling instance claimed it within the last {ClaimWindowMinutes} minute(s)")]
  static partial void LogAuditCycleAlreadyClaimed(ILogger logger, double claimWindowMinutes);

  [LoggerMessage(EventId = 90, Level = LogLevel.Warning,
    Message = "SWEEP: {EpochsDrifted} of {EpochsChecked} sealed epoch(s) DRIFTED and were refolded — " +
              "an unaccounted write path touched sealed history; manifest answers served from those seals were wrong until now")]
  static partial void LogEpochDrift(ILogger logger, int epochsDrifted, int epochsChecked);

  [LoggerMessage(EventId = 91, Level = LogLevel.Information,
    Message = "SWEEP: {EpochsChecked} sealed epoch(s) verified clean against the store recompute")]
  static partial void LogEpochsVerified(ILogger logger, int epochsChecked);
}
