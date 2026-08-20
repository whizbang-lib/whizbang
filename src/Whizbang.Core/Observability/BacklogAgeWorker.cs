using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Observability;

/// <summary>
/// Tuning for the backlog-age duty (topology arc phase 10).
/// </summary>
/// <docs>operations/observability/managed-resource-health#backlog-age</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/BacklogAgeDutyTests.cs:Options_DefaultsAreTheDocumentedPostureAsync</tests>
public sealed class BacklogAgeOptions {
  /// <summary>Run the duty (default true). Disabled ⇒ no peeks, no gauges, no health signal.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Peek cadence (default 1 minute). One management operation per entity per tick: a rounding
  /// error against a Standard namespace's request pool, which matters — an expensive observer of
  /// idle churn would be the same bug it exists to detect.
  /// </summary>
  public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

  /// <summary>
  /// How old an entity's oldest waiting message may get before health degrades (default 15
  /// minutes). Deliberately far above any normal burst drain: the signal must mean "not draining",
  /// not "busy".
  /// </summary>
  public TimeSpan AgeThreshold { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// The backlog-age duty (topology arc phase 10, spec increment 4b): a cheap scheduled peek of
/// subscription depth and oldest-enqueue age per class; a backlog older than
/// <see cref="BacklogAgeOptions.AgeThreshold"/> degrades the <c>backlog</c> health component with
/// the entity named, and every reading feeds the traffic-class gauges.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="TableStatisticsCollector"/> — a periodic <see cref="BackgroundService"/>
/// refreshing gauge caches, with a public once-through method as the deterministic test seam —
/// because the repo has no separate scheduling abstraction: <c>IDutyElector</c> is leader election,
/// not scheduling, and this duty is deliberately per-instance (each instance observes the entities
/// IT consumes from).
/// </para>
/// <para>
/// Peek failures are swallowed per transport: a management-plane hiccup must not fault the
/// observer, and the next tick retries.
/// </para>
/// </remarks>
/// <docs>operations/observability/managed-resource-health#backlog-age</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/BacklogAgeDutyTests.cs</tests>
public sealed partial class BacklogAgeWorker : BackgroundService {
  private readonly IReadOnlyList<IBacklogPeek> _peeks;
  private readonly IReadOnlyList<ITrafficClassOpsRateSource> _opsRateSources;
  private readonly BacklogAgeOptions _options;
  private readonly BacklogAgeState _state;
  private readonly BacklogAgeMetrics _metrics;
  private readonly ILogger<BacklogAgeWorker> _logger;

  /// <summary>Creates the duty.</summary>
  /// <param name="peeks">Every transport's admin-plane peek; empty ⇒ the duty is inert.</param>
  /// <param name="opsRateSources">Every transport's per-namespace ops-rate projection.</param>
  /// <param name="options">Duty tuning.</param>
  /// <param name="state">Shared state the health source projects.</param>
  /// <param name="metrics">The traffic-class gauge caches.</param>
  /// <param name="logger">Logger.</param>
  /// <exception cref="ArgumentNullException">Thrown when a required dependency is null.</exception>
  public BacklogAgeWorker(
      IEnumerable<IBacklogPeek> peeks,
      IEnumerable<ITrafficClassOpsRateSource> opsRateSources,
      IOptions<BacklogAgeOptions> options,
      BacklogAgeState state,
      BacklogAgeMetrics metrics,
      ILogger<BacklogAgeWorker> logger) {
    ArgumentNullException.ThrowIfNull(peeks);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(opsRateSources);
    _peeks = [.. peeks];
    _opsRateSources = [.. opsRateSources];
    _options = options.Value;
    _state = state ?? throw new ArgumentNullException(nameof(state));
    _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.Enabled || (_peeks.Count == 0 && _opsRateSources.Count == 0)) {
      return;
    }

    LogStarted(_logger, _peeks.Count, _options.Interval.TotalSeconds, _options.AgeThreshold.TotalMinutes);

    using var timer = new PeriodicTimer(_options.Interval);
    while (!stoppingToken.IsCancellationRequested) {
      try {
        if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
          return;
        }
        await PeekOnceAsync(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        return;  // graceful shutdown
      }
    }
  }

  /// <summary>
  /// Runs one peek across every wired transport, refreshing the gauges and the health state.
  /// Public so tests drive a deterministic single pass instead of racing the timer.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when the pass finishes.</returns>
  public async Task PeekOnceAsync(CancellationToken cancellationToken) {
    var findings = new List<BacklogAgeFinding>();
    var gauges = new Dictionary<string, BacklogAgeMetrics.BacklogGaugeSample>(StringComparer.Ordinal);

    foreach (var peek in _peeks) {
      IReadOnlyList<BacklogSample> samples;
      try {
        samples = await peek.PeekAsync(cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        // A management-plane hiccup must never take down the observer; next tick retries.
        LogPeekFailed(_logger, peek.TransportName, ex);
        continue;
      }

      foreach (var sample in samples) {
        gauges[sample.Entity] = new BacklogAgeMetrics.BacklogGaugeSample(
          sample.Transport, sample.TransportNamespace, sample.TrafficClass,
          sample.Depth, sample.OldestAge?.TotalSeconds);

        if (sample.OldestAge is not { } age) {
          // Capability honesty: no broker-supplied timestamp on this surface. Report it rather
          // than treat "unknown" as "young" — a silently inert detector is the failure mode this
          // whole arc keeps re-learning.
          _state.ReportUnknownAge(sample.Transport, sample.Entity);
          continue;
        }

        if (age > _options.AgeThreshold) {
          findings.Add(new BacklogAgeFinding(
            sample.Entity, sample.Transport, sample.TransportNamespace, sample.TrafficClass,
            sample.Depth, age));
        }
      }
    }

    _metrics.UpdateBacklogs(gauges);
    _updateOpsRates();
    _state.Replace(findings);

    if (findings.Count > 0) {
      LogAgedBacklog(_logger, findings.Count, findings[0].Entity, findings[0].OldestAge.TotalMinutes);
    }
  }

  /// <summary>
  /// Refreshes the per-class ops-rate gauges. Published on the SAME tick as the backlog readings
  /// deliberately: an operator correlating "this namespace is at its ceiling" with "this class is
  /// backed up" needs both from the same moment, not from two independently-scheduled collectors.
  /// </summary>
  private void _updateOpsRates() {
    if (_opsRateSources.Count == 0) {
      return;
    }

    var rates = new Dictionary<string, BacklogAgeMetrics.OpsRateGaugeSample>(StringComparer.Ordinal);
    foreach (var source in _opsRateSources) {
      foreach (var rate in source.Project()) {
        rates[rate.TransportNamespace] = new BacklogAgeMetrics.OpsRateGaugeSample(
          source.TransportName, rate.TransportNamespace, rate.TrafficClass, rate.OpsPerSecond);
      }
    }

    _metrics.UpdateOpsRates(rates);
  }

  [LoggerMessage(EventId = 70, Level = LogLevel.Information,
    Message = "Backlog-age duty started: {PeekCount} transport peek(s), every {IntervalSeconds}s, degrading past {ThresholdMinutes} minute(s)")]
  private static partial void LogStarted(ILogger logger, int peekCount, double intervalSeconds, double thresholdMinutes);

  [LoggerMessage(EventId = 71, Level = LogLevel.Warning,
    Message = "Backlog-age peek failed for transport {Transport}; retrying next tick")]
  private static partial void LogPeekFailed(ILogger logger, string transport, Exception exception);

  [LoggerMessage(EventId = 72, Level = LogLevel.Warning,
    Message = "Backlog-age duty: {Count} entity(ies) over threshold; oldest is {Entity} at {AgeMinutes} minute(s)")]
  private static partial void LogAgedBacklog(ILogger logger, int count, string entity, double ageMinutes);
}
