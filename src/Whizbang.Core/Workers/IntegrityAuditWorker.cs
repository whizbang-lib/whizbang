using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
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
/// identity digests of this consumer's subscribed types. The manifests come back targeted;
/// comparison happens in the manifest receptor.</item>
/// </list>
/// </summary>
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityAuditWorkerTests.cs</tests>
public sealed partial class IntegrityAuditWorker(
  IServiceScopeFactory scopeFactory,
  ISchemaReadyGate schemaReadyGate,
  IOptions<StreamIntegrityOptions> options,
  ILogger<IntegrityAuditWorker> logger) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<IntegrityAuditWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private int _cycleCount;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.AuditEnabled) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
      return;
    }
    LogStarted(_logger, _options.AuditIntervalMinutes);

    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
    } catch (OperationCanceledException) {
      return;
    }

    var firstCycle = true;
    while (!stoppingToken.IsCancellationRequested) {
      // Startup-first by default: the first audit fires after a jittered startup window so
      // historical divergence heals minutes after a deploy — the jitter de-synchronizes a fleet
      // rollout and A1c's type-level exchange keeps each audit at O(types) wire cost. Opting out
      // (AuditOnStartup=false) restores interval-first scheduling; Phase B covers the fresh
      // window either way.
      var delay = firstCycle
        ? ComputeFirstAuditDelay(_options, Random.Shared.NextDouble)
        : TimeSpan.FromMinutes(_options.AuditIntervalMinutes);
      firstCycle = false;
      try {
        await Task.Delay(delay, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
      try {
        await RunAuditOnceAsync(stoppingToken);
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
    options.AuditOnStartup
      ? TimeSpan.FromSeconds(30 + (Math.Max(0, options.StartupAuditMaxJitterSeconds) * jitter()))
      : TimeSpan.FromMinutes(options.AuditIntervalMinutes);

  /// <summary>One audit cycle: the local coverage half, then the cross-service manifest requests.</summary>
  public async Task RunAuditOnceAsync(CancellationToken cancellationToken) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var coordinator = services.GetService<IWorkCoordinator>();
    var dispatcher = services.GetService<IDispatcher>();
    if (coordinator is null || dispatcher is null) {
      return;
    }
    var settle = TimeSpan.FromMinutes(_options.AuditSettleWindowMinutes);
    var metrics = services.GetService<Observability.StreamIntegrityMetrics>();

    // ── A1c: the full-sweep cadence ──────────────────────────────────────
    // Steady-state cycles run on the incrementally-maintained digest table (O(buckets), with
    // settle-skip on busy buckets). Every Nth cycle is the trust-but-verify sweep: heal our OWN
    // table against a full recompute (non-zero drift = an unaccounted write path — alarm), and
    // exchange recomputed digests end to end so busy buckets get their coverage too.
    var cycle = Interlocked.Increment(ref _cycleCount);
    var sweep = _options.FullSweepEveryNthAudit > 0 && cycle % _options.FullSweepEveryNthAudit == 0;
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
        ReportStreamId = TrackedGuid.NewMedo().Value,
        PerspectiveName = gap.PerspectiveName,
        GapStreamId = gap.StreamId,
        EventCount = gap.EventCount,
        AutoRebuildRequested = autoRebuild,
      }).ConfigureAwait(false);
      LogCoverageGap(_logger, gap.PerspectiveName, gap.StreamId, gap.EventCount, autoRebuild);
    }

    // ── A: cross-service manifest requests ───────────────────────────────
    var tracker = services.GetService<IntegrityGapTracker>();
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var typeProvider = services.GetService<IEventTypeProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = _options.RepairTopic
      ?? services.GetService<TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
    if (tracker is null || transport is null || serializer is null
        || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return;   // no cross-service infrastructure — the local half already ran.
    }

    var subscribed = typeProvider?.GetEventTypes()
      .Select(TypeNameFormatter.FormatClrTypeName)
      .Distinct(StringComparer.Ordinal)
      .ToList();
    foreach (var (originId, originName) in tracker.GetOrigins()) {
      var envelope = new MessageEnvelope<RequestIntegrityManifest> {
        MessageId = new MessageId(TrackedGuid.NewMedo()),
        Payload = new RequestIntegrityManifest {
          RequesterService = requester,
          Topic = topic,
          EventTypes = subscribed,
          Level = ManifestLevel.Types,
          UseRecompute = sweep,
        },
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTimeOffset.UtcNow,
            ServiceInstance = instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown
          }
        ],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
        Target = originName,
      };
      var serialized = serializer.SerializeEnvelope(envelope);
      await transport.PublishAsync(serialized.JsonEnvelope, new TransportDestination(topic), serialized.EnvelopeType,
        cancellationToken: cancellationToken).ConfigureAwait(false);
      metrics?.ManifestsRequested.Add(1,
        new KeyValuePair<string, object?>("origin", originName),
        new KeyValuePair<string, object?>("sweep", sweep));
      LogManifestRequested(_logger, originName, originId);
    }
  }

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
}
