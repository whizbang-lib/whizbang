using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Periodic scanner that drives DLQ recovery — fetches DLQ rows that are ready, applies
/// the configured <see cref="IDeadLetterRecoveryPolicy"/>, and either re-emits to the
/// source work table or marks terminal (HoldForReview / PermanentlyFailed).
/// </summary>
/// <remarks>
/// <para>
/// Cadence: <see cref="DeadLetterRecoveryOptions.ScanIntervalMinutes"/> backstop, default
/// 10 min. On startup, runs the generation-replay sweep once so rows from prior
/// generations get exactly one auto-retry on the new build.
/// </para>
/// <para>
/// The worker is process-singleton — multiple replicas all run their scan loops, and they
/// race on each row via the atomic <c>recover_dead_letter</c> SQL (which transitions to
/// Recovering inside its UPDATE). Whichever instance wins drains; the others see false
/// returns and move on. No coordination needed.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/recovery</docs>
/// <docs>operations/workers/housekeeping-arbitration</docs>
/// <docs>operations/dead-letter-queue/canary-recovery</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterRecoveryWorkerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/RecoveryLifecycleHardeningTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterCanaryCampaignTests.cs</tests>
public partial class DeadLetterRecoveryWorker(
  IServiceScopeFactory scopeFactory,
  ISchemaReadyGate schemaReadyGate,
  IOptions<DeadLetterRecoveryOptions> options,
  IGenerationProvider generationProvider,
  ILogger<DeadLetterRecoveryWorker> logger,
  DeadLetterMetrics? metrics = null,
  Whizbang.Core.Notifications.IWorkNotificationListener? notificationListener = null,
  HousekeepingCoordinator? housekeeping = null,
  Whizbang.Core.Observability.HousekeepingMetrics? metricsRollup = null
) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly DeadLetterRecoveryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IGenerationProvider _generationProvider = generationProvider ?? throw new ArgumentNullException(nameof(generationProvider));
  private readonly ILogger<DeadLetterRecoveryWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  // Optional: an unwired host keeps the pre-arbitration behavior rather than losing recovery.
  private readonly HousekeepingCoordinator? _housekeeping = housekeeping;
  private readonly Whizbang.Core.Observability.HousekeepingMetrics? _metricsRollup = metricsRollup;
  private readonly DeadLetterMetrics? _metrics = metrics;
  private readonly Whizbang.Core.Notifications.IWorkNotificationListener? _notificationListener = notificationListener;
  private readonly SemaphoreSlim _wake = new(0, 1);
  private bool _signalSubscribed;

  /// <summary>NOTIFY signal handler — wakes on a DeadLetterReady signal so the next scan runs within ms.</summary>
  private void _onSignal(Whizbang.Core.Notifications.WorkSignalCategory category) {
    if (category != Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady) {
      return;
    }
    try { _wake.Release(); } catch (SemaphoreFullException) { /* coalesce */ }
  }

  /// <inheritdoc />
  public override Task StopAsync(CancellationToken cancellationToken) {
    if (_signalSubscribed && _notificationListener is not null) {
      _notificationListener.OnSignal -= _onSignal;
      _signalSubscribed = false;
    }
    return base.StopAsync(cancellationToken);
  }

  private int _noServiceLogged;

  private void _logNoRecoveryServiceOnce() {
    if (Interlocked.Exchange(ref _noServiceLogged, 1) == 0) {
      LogNoRecoveryService(_logger);
    }
  }

  /// <summary>
  /// Policy cooldown scaled 2^attempts, capped at 24 hours: a flat cooldown turns a
  /// persistent failure into a metronome; uncapped growth schedules a row into next month.
  /// A zero base stays zero (immediate-retry policies keep their semantics).
  /// </summary>
  private static TimeSpan _exponentialCooldown(TimeSpan baseCooldown, int attempts) {
    if (baseCooldown <= TimeSpan.Zero) {
      return TimeSpan.Zero;
    }
    var factor = Math.Pow(2, Math.Min(attempts, 10));
    var scaled = TimeSpan.FromTicks((long)Math.Min(baseCooldown.Ticks * factor, TimeSpan.FromHours(24).Ticks));
    return scaled;
  }

  private long _totalScans;
  private long _totalRecovered;
  private long _totalHeld;
  private long _totalPermanentlyFailed;
  private long _totalGenerationReplays;
  private DateTimeOffset? _previousScanStartedAt;
  private int _consecutiveSelfInflicted;
  private DateTimeOffset? _breakerOpenedAt;
  private long _totalLoopBreakerTrips;

  /// <summary>Number of scan cycles since process start.</summary>
  public long TotalScans => Interlocked.Read(ref _totalScans);
  /// <summary>Cumulative count of DLQ rows successfully recovered.</summary>
  public long TotalRecovered => Interlocked.Read(ref _totalRecovered);
  /// <summary>Cumulative count of rows policy-exhausted to HoldForReview.</summary>
  public long TotalHeld => Interlocked.Read(ref _totalHeld);
  /// <summary>Cumulative count of rows policy-exhausted to PermanentlyFailed.</summary>
  public long TotalPermanentlyFailed => Interlocked.Read(ref _totalPermanentlyFailed);
  /// <summary>Total rows scheduled by the generation-replay sweep on startup.</summary>
  public long TotalGenerationReplays => Interlocked.Read(ref _totalGenerationReplays);

  /// <summary>How many times the loop breaker has suspended recovery in this process.</summary>
  public long TotalLoopBreakerTrips => Interlocked.Read(ref _totalLoopBreakerTrips);

  /// <summary>Whether recovery is currently suspended by the loop breaker.</summary>
  public bool IsLoopBreakerOpen => _breakerOpenedAt is not null;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.ScanIntervalMinutes);

    // Slice 7c — subscribe to the DeadLetterReady NOTIFY signal. The wh_dead_letters
    // AFTER INSERT trigger (migration 056) fires this on every new DLQ row so the
    // worker wakes within ms instead of waiting up to ScanIntervalMinutes.
    if (_notificationListener is not null && !_signalSubscribed) {
      _notificationListener.OnSignal += _onSignal;
      _signalSubscribed = true;
    }

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

    // Generation-replay sweep — exactly-once per (row, generation) so this is safe to
    // run on every pod startup.
    if (_options.EnableGenerationReplay) {
      try {
        var current = _generationProvider.GetGeneration();
        using var scope = _scopeFactory.CreateScope();
        // GetService (not GetRequiredService) — when no persistence layer is wired
        // (InMemory samples, unit-test hosts) the worker degrades to a no-op rather
        // than throwing at startup. Same pattern as IDeadLetterStore wiring elsewhere
        // in the DLQ surface.
        var svc = scope.ServiceProvider.GetService<IDeadLetterRecoveryService>();
        if (svc is null) {
          // Loudly, and only the sweep: the old code returned here, which killed the WHOLE
          // worker — a host missing the service looked identical to a healthy quiet one,
          // and in production 20,000 due rows sat behind that silence for a day.
          _logNoRecoveryServiceOnce();
        } else {
          var scheduled = await svc.ResetForGenerationAsync(current, _options.GenerationReplayStaggerMinutes, stoppingToken).ConfigureAwait(false);
          if (scheduled > 0) {
            Interlocked.Add(ref _totalGenerationReplays, scheduled);
            LogGenerationReplay(_logger, scheduled, current);
            _metrics?.GenerationReplayScheduled.Add(scheduled,
              new KeyValuePair<string, object?>("generation", current));
          }
        }
      } catch (Exception ex) {
        LogError(_logger, ex);
      }
    }

    var campaignMode = _options.RetryHeldOnStartup;
    if (campaignMode == RetryHeldOnStartupMode.Off
        && _options.AutoCanaryOnNewGeneration
        && Interlocked.Read(ref _totalGenerationReplays) > 0) {
      // A new build generation re-offered rows from older builds: held cohorts are
      // evidence about an OLD build, so re-test the hypothesis automatically — at probe
      // cost, never storm cost. An explicit operator mode always wins over this default.
      campaignMode = RetryHeldOnStartupMode.Canary;
    }
    if (campaignMode != RetryHeldOnStartupMode.Off) {
      try {
        await _startHeldCampaignAsync(campaignMode, stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        return;
      } catch (Exception ex) {
        LogError(_logger, ex);
      }
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await _scanOnceAsync(stoppingToken);
        Interlocked.Increment(ref _totalScans);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogError(_logger, ex);
      }

      try {
        // Slice 7c — race the polling interval against the NOTIFY-driven wake. When the
        // listener fires, the next scan runs within ms; otherwise the ScanIntervalMinutes
        // backstop poll still kicks in. When no listener is wired the wake task never
        // completes so behaviour collapses to the legacy polling-only loop.
        var pollDelay = Task.Delay(TimeSpan.FromMinutes(_options.ScanIntervalMinutes), stoppingToken);
        var wakeTask = _notificationListener is not null
          ? _wake.WaitAsync(stoppingToken)
          : new TaskCompletionSource<bool>().Task;
        await Task.WhenAny(pollDelay, wakeTask).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      }
    }

    LogStopped(_logger);
  }

  /// <summary>
  /// Whether the loop breaker is currently suppressing recovery, closing it when the cooldown has
  /// elapsed so a transient condition recovers without an operator.
  /// </summary>
  private bool _isBreakerOpen(DateTimeOffset now) {
    if (_breakerOpenedAt is not { } openedAt) { return false; }
    // Cooldown 0 means stay open until the process restarts: the deliberate choice for a
    // deployment where an operator wants to look before recovery runs again.
    if (_options.LoopBreakerCooldownMinutes <= 0) { return true; }
    if (now - openedAt < TimeSpan.FromMinutes(_options.LoopBreakerCooldownMinutes)) { return true; }
    _breakerOpenedAt = null;
    _consecutiveSelfInflicted = 0;
    LogLoopBreakerClosed(_logger, _options.LoopBreakerCooldownMinutes);
    return false;
  }

  // Canary campaigns in flight for THIS process's generation. Fingerprints only — the
  // persisted campaign row is the durable record; this set is merely "evaluate these on
  // each scan", rebuilt on restart by _startHeldCampaignAsync (BeginCanaryProbesAsync is
  // idempotent per (fingerprint, generation), so re-listing cohorts resumes campaigns).
  private readonly HashSet<string> _campaignsInFlight = [];
  // Mixed cohorts in trickle release: fingerprint -> (wave number, last wave size).
  private readonly Dictionary<string, (int Wave, int LastSize)> _tricklesInFlight = [];
  private string? _campaignGeneration;

  private async Task _startHeldCampaignAsync(RetryHeldOnStartupMode mode, CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var svc = scope.ServiceProvider.GetService<IDeadLetterRecoveryService>();
    if (svc is null) {
      _logNoRecoveryServiceOnce();
      return;
    }

    // Grandfather gate first: campaigns operate only on rows the machinery can re-drive.
    var purged = await svc.PurgeUndeliverableHeldAsync(ct).ConfigureAwait(false);
    if (purged > 0) {
      LogCampaignPurgedUndeliverable(_logger, purged);
    }

    var cohorts = await svc.ListHeldCohortsAsync(ct).ConfigureAwait(false);
    if (cohorts.Count == 0) {
      return;
    }
    _campaignGeneration = _generationProvider.GetGeneration();
    LogCampaignStarted(_logger, mode, cohorts.Count, _campaignGeneration);

    var stagger = TimeSpan.FromMinutes(_options.ReleaseStaggerMinutes);
    foreach (var cohort in cohorts) {
      ct.ThrowIfCancellationRequested();
      if (mode == RetryHeldOnStartupMode.Full) {
        var released = await svc.ReleaseHeldCohortAsync(cohort.Fingerprint, stagger, ct).ConfigureAwait(false);
        LogCohortReleased(_logger, cohort.Fingerprint, released, "full");
      } else {
        var probes = await svc.BeginCanaryProbesAsync(
          cohort.Fingerprint, _campaignGeneration, _options.CanaryProbeSize,
          _options.GenerationBudget, ct).ConfigureAwait(false);
        if (probes < 0) {
          // The store says this cohort has failed campaigns on GenerationBudget distinct
          // generations: permanently pending operator. Said loudly — this is a decision point.
          LogCohortBudgetExhausted(_logger, cohort.Fingerprint, cohort.RowCount);
          continue;
        }
        // probes == 0 means the campaign already exists (restart mid-campaign) — resume
        // evaluating it rather than orphaning it.
        LogProbesStarted(_logger, cohort.Fingerprint, probes, cohort.RowCount, cohort.MessageTypeCount);
        _campaignsInFlight.Add(cohort.Fingerprint);
      }
    }
  }

  /// <summary>
  /// Terminal transition for an exhausted row: HoldForReview or PermanentlyFailed per the
  /// rule. Extracted so the #681 pass-verdict bypass and the plain path share one body.
  /// </summary>
  private async Task _transitionExhaustedAsync(
      IDeadLetterRecoveryService svc, DeadLetterEntry entry, RecoveryPolicy rule, CancellationToken ct) {
    try {
      if (rule.HoldForReviewAfterExhaustion) {
        await svc.MarkHoldingAsync(entry.DeadLetterId, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _totalHeld);
        LogHeld(_logger, entry.DeadLetterId, rule.Name);
        _metrics?.Held.Add(1,
          new KeyValuePair<string, object?>("policy_name", rule.Name),
          new KeyValuePair<string, object?>("reason", entry.FailureReason.ToString()));
      } else {
        await svc.MarkPermanentlyFailedAsync(entry.DeadLetterId, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _totalPermanentlyFailed);
        LogPermanentlyFailed(_logger, entry.DeadLetterId, rule.Name);
        _metrics?.PermanentlyFailed.Add(1,
          new KeyValuePair<string, object?>("policy_name", rule.Name),
          new KeyValuePair<string, object?>("reason", entry.FailureReason.ToString()));
      }
    } catch (Exception ex) {
      LogTerminalSetFailed(_logger, entry.DeadLetterId, ex);
    }
  }

  private async Task _evaluateCampaignsAsync(IDeadLetterRecoveryService svc, CancellationToken ct) {
    if (_campaignsInFlight.Count == 0 || _campaignGeneration is null) {
      return;
    }
    var stagger = TimeSpan.FromMinutes(_options.ReleaseStaggerMinutes);
    foreach (var fingerprint in _campaignsInFlight.ToArray()) {
      ct.ThrowIfCancellationRequested();
      var verdict = await svc.EvaluateCampaignAsync(fingerprint, _campaignGeneration, ct).ConfigureAwait(false);
      switch (verdict.Kind) {
        case CanaryVerdictKind.Pending:
          // Issue #682: Pending with NOTHING outstanding and NOTHING counted means the
          // campaign's probe rows were destroyed (the retention purge can delete evidence
          // out from under a live campaign). Re-evaluating the same emptiness every scan
          // waits forever — re-mint probes instead. BeginCanaryProbesAsync refreshes a
          // probe-less unresolved campaign from the surviving held rows.
          if (verdict.ProbesOutstanding == 0 && verdict.ProbesSucceeded == 0 && verdict.ProbesFailed == 0) {
            var refreshed = await svc.BeginCanaryProbesAsync(
              fingerprint, _campaignGeneration, _options.CanaryProbeSize,
              _options.GenerationBudget, ct).ConfigureAwait(false);
            LogCampaignEvidenceLost(_logger, fingerprint, refreshed);
          }
          break;
        case CanaryVerdictKind.Pass:
          var released = await svc.ReleaseHeldCohortAsync(fingerprint, stagger, ct).ConfigureAwait(false);
          LogCohortReleased(_logger, fingerprint, released, "canary-pass");
          _metrics?.RecordCohortVerdict(fingerprint, CanaryVerdictKind.Pass);
          _campaignsInFlight.Remove(fingerprint);
          break;
        case CanaryVerdictKind.Fail:
          LogCohortFailed(_logger, fingerprint, verdict.ProbesFailed);
          _metrics?.RecordCohortVerdict(fingerprint, CanaryVerdictKind.Fail);
          _campaignsInFlight.Remove(fingerprint);
          break;
        case CanaryVerdictKind.Mixed:
        default:
          LogCohortMixed(_logger, fingerprint, verdict.ProbesSucceeded, verdict.ProbesFailed);
          _metrics?.RecordCohortVerdict(fingerprint, CanaryVerdictKind.Mixed);
          _campaignsInFlight.Remove(fingerprint);
          // A Mixed cohort earns trust in doublings, not in one blind release: enter the
          // trickle with a probe-sized first wave, immediately.
          var firstWave = await svc.BeginTrickleWaveAsync(
            fingerprint, _campaignGeneration, _options.CanaryProbeSize, ct).ConfigureAwait(false);
          if (firstWave > 0) {
            _tricklesInFlight[fingerprint] = (1, _options.CanaryProbeSize);
            LogTrickleWave(_logger, fingerprint, 1, firstWave);
          }
          break;
      }
    }
  }

  private async Task _evaluateTricklesAsync(IDeadLetterRecoveryService svc, CancellationToken ct) {
    if (_tricklesInFlight.Count == 0 || _campaignGeneration is null) {
      return;
    }
    foreach (var (fingerprint, state) in _tricklesInFlight.ToArray()) {
      ct.ThrowIfCancellationRequested();
      var washback = await svc.CountWaveRequarantinesAsync(fingerprint, _campaignGeneration, ct).ConfigureAwait(false);
      if (washback > 0) {
        // Dirty wave: halt. The remainder stays held rather than feeding a failure at
        // doubling volume; the released waves already out stay out.
        LogTrickleHalted(_logger, fingerprint, state.Wave, washback);
        _metrics?.RecordReleaseWave(fingerprint, clean: false);
        _tricklesInFlight.Remove(fingerprint);
        continue;
      }
      _metrics?.RecordReleaseWave(fingerprint, clean: true);
      var nextSize = state.LastSize * 2;
      var released = await svc.BeginTrickleWaveAsync(fingerprint, _campaignGeneration, nextSize, ct).ConfigureAwait(false);
      if (released == 0) {
        LogTrickleDrained(_logger, fingerprint, state.Wave);
        _tricklesInFlight.Remove(fingerprint);
        continue;
      }
      _tricklesInFlight[fingerprint] = (state.Wave + 1, nextSize);
      LogTrickleWave(_logger, fingerprint, state.Wave + 1, released);
    }
  }

  private async Task _scanOnceAsync(CancellationToken ct) {
    var scanStartedAt = DateTimeOffset.UtcNow;
    using var scope = _scopeFactory.CreateScope();
    // Housekeeping arbitration. Recovery holds the HIGHEST rank, because the dead-letter table
    // frequently contains the very messages integrity would otherwise detect as gaps and ask an
    // origin to redeliver over the wire — healing locally removes the reason to ask. It is still
    // gated on settledness: re-driving puts work back onto the same queues, so doing it mid-drain
    // is how a recovery becomes a second storm.
    HousekeepingCoordinator.Decision? housekeeping = null;
    if (_housekeeping is not null && _options.WaitForIdle) {
      var coordinatorForBacklog = scope.ServiceProvider.GetService<IWorkCoordinator>();
      ServiceBacklog? backlog = null;
      if (coordinatorForBacklog is not null) {
        try {
          backlog = await coordinatorForBacklog.CountServiceBacklogAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          LogError(_logger, ex);
        }
      }
      var decision = _housekeeping.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, backlog);
      if (!decision.Granted) {
        LogRecoveryDeferred(_logger, decision.Reason, backlog?.UnprocessedInboxRows ?? -1);
        return;
      }
      housekeeping = decision;
    }
    var scanRecovered = 0;
    try {
      // Optional — no persistence layer wired = no scanning. Same as the startup-replay
      // branch above; keeps the worker safe to register everywhere. Said once, not swallowed:
      // silent absence is indistinguishable from healthy quiet.
      var svc = scope.ServiceProvider.GetService<IDeadLetterRecoveryService>();
      if (svc is null) {
        _logNoRecoveryServiceOnce();
        return;
      }

      // Canary campaigns evaluate on the scan cadence, under the same arbitration grant —
      // and BEFORE the due-fetch early-return below, because a quiet queue is exactly when
      // verdicts land (probes recovered = nothing due = empty batch).
      await _evaluateCampaignsAsync(svc, ct).ConfigureAwait(false);
      await _evaluateTricklesAsync(svc, ct).ConfigureAwait(false);

      // Stack backfill: the async half of the stack contract. Bounded per scan, and the
      // C# normalizer is the SAME implementation the inline metric uses, so both halves
      // agree on every stack_id.
      if (_options.StackBackfillBatchSize > 0) {
        var unstacked = await svc.FetchUnstackedAsync(_options.StackBackfillBatchSize, ct).ConfigureAwait(false);
        // Normalize in-process, then record the whole batch in ONE round trip — a
        // storm-sized backfill was N per-row calls before.
        var batch = new List<(Guid, Whizbang.Core.DeadLetters.StackIdentity)>(unstacked.Count);
        foreach (var row in unstacked) {
          ct.ThrowIfCancellationRequested();
          var stack = Whizbang.Core.DeadLetters.StackNormalizer.Normalize(row.ErrorText);
          if (stack is not null) {
            batch.Add((row.DeadLetterId, stack));
          }
        }
        if (batch.Count > 0) {
          var newStacks = await svc.RecordStacksAsync(batch, ct).ConfigureAwait(false);
          _metrics?.RecordNewStacks(newStacks);
        }

        // Roll the stack-history window on the same idle-gated scan. Skipped entirely when
        // retention is non-positive, so a keep-forever configuration costs no round trip.
        // The DELETE is cheap and day-granular, so running it each scan is a near no-op.
        if (_options.StackHistoryRetentionDays > 0) {
          var pruned = await svc.PruneStackHistoryAsync(_options.StackHistoryRetentionDays, ct).ConfigureAwait(false);
          if (pruned > 0) {
            LogStackHistoryPruned(_logger, pruned, _options.StackHistoryRetentionDays);
            // The maintenance facet the operator watches: a dedicated counter, and the
            // housekeeping volume rollup under the Maintenance activity (this IS cleanup).
            _metrics?.RecordStackHistoryPruned(pruned);
            _metricsRollup?.RecordItems(HousekeepingCoordinator.Activity.Maintenance, pruned);
          }
        }
      }

      var policy = scope.ServiceProvider.GetRequiredService<IDeadLetterRecoveryPolicy>();

      // Fetch in batches — bounded by ScanBatchSize so a single scan doesn't try to drain
      // an enormous backlog in one breath. Subsequent scans pick up where this one stopped.
      // #669: a pass FORCED through the settledness gate runs narrow. The bounded-deferral
      // escape exists so a never-settled service still heals — but the service is visibly
      // busy, so recovery trickles instead of flooding the queues it is yielding to.
      var batchSize = housekeeping?.Reason == HousekeepingCoordinator.Verdict.ProceedDeferralLimit
        ? Math.Min(Math.Max(1, _options.PressuredScanBatchSize), _options.ScanBatchSize)
        : _options.ScanBatchSize;
      var entries = await svc.FetchDueAsync(batchSize, ct).ConfigureAwait(false);
      if (entries.Count == 0) {
        // A quiet cycle is evidence the cycle is NOT feeding itself, so it clears the consecutive
        // run. Without this, a self-inflicted burst followed by genuine quiet would keep its count
        // and trip later on an unrelated batch.
        _consecutiveSelfInflicted = 0;
        _previousScanStartedAt = scanStartedAt;
        return;
      }

      if (_options.LoopBreakerEnabled) {
        if (_isBreakerOpen(scanStartedAt)) {
          LogLoopBreakerSuppressed(_logger, entries.Count);
          _previousScanStartedAt = scanStartedAt;
          return;
        }

        var timestamps = new DateTimeOffset[entries.Count];
        for (var i = 0; i < entries.Count; i++) {
          timestamps[i] = entries[i].DeadLetteredAt;
        }
        var signal = DeadLetterRecoveryLoopSignal.Measure(
          timestamps, _previousScanStartedAt, _options.LoopBreakerFreshFraction);

        if (signal.IsSelfInflicted) {
          _consecutiveSelfInflicted++;
          if (_consecutiveSelfInflicted >= _options.LoopBreakerConsecutiveCycles) {
            _breakerOpenedAt = scanStartedAt;
            Interlocked.Increment(ref _totalLoopBreakerTrips);
            LogLoopBreakerTripped(
              _logger, signal.Fresh, signal.Considered, _consecutiveSelfInflicted,
              _options.LoopBreakerCooldownMinutes);
            _previousScanStartedAt = scanStartedAt;
            return;
          }
        } else {
          _consecutiveSelfInflicted = 0;
        }
      }

      _previousScanStartedAt = scanStartedAt;

      // #681: fingerprints with a terminal Pass campaign on the CURRENT generation. A Pass
      // is standing evidence about the build — the one-shot canary-pass release freed only
      // the rows held at verdict time, and every scan after it re-held the rest of the
      // cohort. Fetched at most once per scan, and only when an exhausted fingerprinted row
      // actually needs it.
      HashSet<string>? passedFingerprints = null;

      foreach (var entry in entries) {
        if (ct.IsCancellationRequested) { return; }
        if (!policy.ShouldRecover(entry)) { continue; }

        var rule = policy.GetPolicy(entry);

        // Exhaustion check first: if RecoveryAttempts already reached MaxRecoveryAttempts,
        // transition to terminal state per HoldForReviewAfterExhaustion.
        if (entry.RecoveryAttempts >= rule.MaxRecoveryAttempts) {
          // #681: a proven-safe cohort keeps its fresh attempt instead of re-quarantining.
          // Failure still paces itself: a failed re-drive schedules the policy cooldown, so
          // a genuinely broken row retries once per cooldown, never in a hot loop.
          if (rule.HoldForReviewAfterExhaustion && entry.ErrorFingerprint is not null) {
            if (passedFingerprints is null) {
              var passed = await svc.GetPassedCampaignFingerprintsAsync(
                _generationProvider.GetGeneration(), ct).ConfigureAwait(false);
              passedFingerprints = [.. passed];
            }
            if (passedFingerprints.Contains(entry.ErrorFingerprint)) {
              LogExhaustionBypassedByPass(_logger, entry.DeadLetterId, entry.ErrorFingerprint);
            } else {
              await _transitionExhaustedAsync(svc, entry, rule, ct).ConfigureAwait(false);
              continue;
            }
          } else {
            await _transitionExhaustedAsync(svc, entry, rule, ct).ConfigureAwait(false);
            continue;
          }
        }

        // Try the recovery.
        _metrics?.RecoveryAttempts.Add(1,
          new KeyValuePair<string, object?>("reason", entry.FailureReason.ToString()));
        try {
          var recovered = await svc.RecoverAsync(entry.DeadLetterId, ct).ConfigureAwait(false);
          if (recovered) {
            Interlocked.Increment(ref _totalRecovered);
            scanRecovered++;
            LogRecovered(_logger, entry.DeadLetterId, entry.SourceTable);
            _metrics?.Recovered.Add(1,
              new KeyValuePair<string, object?>("source_table", entry.SourceTable));
          } else {
            // recover_dead_letter returned false — row was already terminal or claimed by
            // another worker. No action needed; the other worker's path handles state.
          }
        } catch (Exception ex) {
          // Recovery failed (DB hiccup, transport blip, etc.). Schedule the next attempt
          // per policy cooldown. The recovery_attempts counter was already bumped by the
          // SQL function's UPDATE; the worker will re-evaluate exhaustion on the next scan.
          LogRecoveryAttemptFailed(_logger, entry.DeadLetterId, ex);
          try {
            await svc.ScheduleNextAttemptAsync(
              entry.DeadLetterId,
              DateTimeOffset.UtcNow.Add(_exponentialCooldown(rule.Cooldown, entry.RecoveryAttempts)),
              ct).ConfigureAwait(false);
          } catch (Exception scheduleEx) {
            LogScheduleFailed(_logger, entry.DeadLetterId, scheduleEx);
          }
        }
      }
    } finally {
      // In a finally: a scan that throws and never releases the slot would disable BOTH recovery
      // and every lower-ranked activity for the lifetime of the process.
      if (housekeeping is not null) {
        // Volume rollup before the slot releases: dead letters actually re-driven this cycle.
        _metricsRollup?.RecordItems(HousekeepingCoordinator.Activity.DeadLetterRecovery, scanRecovered);
        _housekeeping?.End(HousekeepingCoordinator.Activity.DeadLetterRecovery);
      }
    }
  }

  [LoggerMessage(EventId = 27, Level = LogLevel.Information,
    Message = "Pruned {Pruned} rolling stack-history row(s) older than {RetentionDays} day(s)")]
  static partial void LogStackHistoryPruned(ILogger logger, int pruned, int retentionDays);

  [LoggerMessage(EventId = 24, Level = LogLevel.Information,
    Message = "Trickle wave {Wave} released {Released} row(s) of Mixed cohort {Fingerprint} — staggered, evaluated next scan")]
  static partial void LogTrickleWave(ILogger logger, string fingerprint, int wave, int released);

  [LoggerMessage(EventId = 25, Level = LogLevel.Warning,
    Message = "Trickle for Mixed cohort {Fingerprint} HALTED at wave {Wave}: {Washback} released row(s) re-dead-lettered. The remainder stays held — releasing further would feed a live failure at doubling volume; the cohort is pending an operator decision")]
  static partial void LogTrickleHalted(ILogger logger, string fingerprint, int wave, int washback);

  [LoggerMessage(EventId = 26, Level = LogLevel.Information,
    Message = "Mixed cohort {Fingerprint} fully DRAINED through {Waves} clean trickle wave(s)")]
  static partial void LogTrickleDrained(ILogger logger, string fingerprint, int waves);

  [LoggerMessage(EventId = 23, Level = LogLevel.Warning,
    Message = "Canary campaign for cohort {Fingerprint} ({Rows} row(s)) is NOT starting: its generation budget is exhausted — probes failed on the configured number of distinct build generations. The cohort is permanently pending an operator decision (release, purge, or hold).")]
  static partial void LogCohortBudgetExhausted(ILogger logger, string fingerprint, long rows);

  [LoggerMessage(EventId = 17, Level = LogLevel.Information,
    Message = "Held-cohort campaign starting: mode={Mode}, cohorts={Cohorts}, generation={Generation}")]
  static partial void LogCampaignStarted(ILogger logger, RetryHeldOnStartupMode mode, int cohorts, string generation);

  [LoggerMessage(EventId = 18, Level = LogLevel.Warning,
    Message = "Campaign purged {Purged} held row(s) with no re-drivable payload — no recovery can ever process them; they are marked PermanentlyFailed for the operator ledger")]
  static partial void LogCampaignPurgedUndeliverable(ILogger logger, int purged);

  [LoggerMessage(EventId = 19, Level = LogLevel.Information,
    Message = "Canary probes started for cohort {Fingerprint}: probes={Probes} (0 = resuming an existing campaign), rows={Rows}, messageTypes={MessageTypes}")]
  static partial void LogProbesStarted(ILogger logger, string fingerprint, int probes, long rows, int messageTypes);

  [LoggerMessage(EventId = 20, Level = LogLevel.Information,
    Message = "Held cohort {Fingerprint} released ({Released} row(s), {Mode}) — rows return to Pending staggered; the paced scans drain them")]
  static partial void LogCohortReleased(ILogger logger, string fingerprint, int released, string mode);

  [LoggerMessage(EventId = 29, Level = LogLevel.Information,
    Message = "DLQ row {DeadLetterId} is exhausted but its cohort {Fingerprint} PASSED its canary on this generation — re-driving instead of holding (#681)")]
  static partial void LogExhaustionBypassedByPass(ILogger logger, Guid deadLetterId, string fingerprint);

  [LoggerMessage(EventId = 28, Level = LogLevel.Warning,
    Message = "Campaign {Fingerprint} lost its probe evidence (0 outstanding / 0 counted) — probe rows were purged or deleted mid-campaign; re-minted {Refreshed} probe(s) from the surviving held rows")]
  static partial void LogCampaignEvidenceLost(ILogger logger, string fingerprint, int refreshed);

  [LoggerMessage(EventId = 21, Level = LogLevel.Information,
    Message = "Canary campaign for cohort {Fingerprint} FAILED ({ProbesFailed} probe(s) re-dead-lettered) — the cohort stays held; the bug is still live")]
  static partial void LogCohortFailed(ILogger logger, string fingerprint, int probesFailed);

  [LoggerMessage(EventId = 22, Level = LogLevel.Warning,
    Message = "Canary campaign for cohort {Fingerprint} returned MIXED: {ProbesSucceeded} probe(s) recovered, {ProbesFailed} re-dead-lettered. The cohort likely spans more than one real failure; it stays held for operator review — auto-releasing would re-drive the failing part at full volume")]
  static partial void LogCohortMixed(ILogger logger, string fingerprint, int probesSucceeded, int probesFailed);

  [LoggerMessage(EventId = 16, Level = LogLevel.Warning,
    Message = "IDeadLetterRecoveryService is not registered: dead-letter recovery cannot scan and "
            + "no dead-lettered rows will be re-driven on this host. Wire a persistence driver "
            + "(e.g. the Postgres data package) or set Whizbang:DeadLetterRecovery:Enabled=false "
            + "to silence this worker deliberately.")]
  static partial void LogNoRecoveryService(ILogger logger);

  [LoggerMessage(EventId = 15, Level = LogLevel.Debug,
    Message = "DeadLetterRecoveryWorker deferred: {Reason} (unprocessed inbox rows={InboxRows})")]
  static partial void LogRecoveryDeferred(ILogger logger, HousekeepingCoordinator.Verdict reason, long inboxRows);

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "DeadLetterRecoveryWorker started: scanIntervalMinutes={ScanIntervalMinutes}")]
  static partial void LogStarted(ILogger logger, int scanIntervalMinutes);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "DeadLetterRecoveryWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "DeadLetterRecoveryWorker disabled via options")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "DeadLetterRecoveryWorker scan cycle failed; will retry on next interval")]
  static partial void LogError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information,
    Message = "DeadLetterRecoveryWorker generation-replay scheduled {Count} row(s) for current generation '{Generation}'")]
  static partial void LogGenerationReplay(ILogger logger, int count, string generation);

  [LoggerMessage(EventId = 12, Level = LogLevel.Error,
    Message = "DeadLetterRecoveryWorker SUSPENDED recovery: {Fresh} of {Considered} rows in this batch were dead-lettered after the previous scan began, for {Cycles} consecutive cycles — recovery is re-creating the dead letters it is clearing. Recovery is off for {CooldownMinutes} minute(s); dead letters accumulate meanwhile and the underlying failure needs fixing")]
  static partial void LogLoopBreakerTripped(ILogger logger, int fresh, int considered, int cycles, int cooldownMinutes);

  [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
    Message = "DeadLetterRecoveryWorker is suspended by the loop breaker; skipped {Count} due row(s) this cycle")]
  static partial void LogLoopBreakerSuppressed(ILogger logger, int count);

  [LoggerMessage(EventId = 14, Level = LogLevel.Information,
    Message = "DeadLetterRecoveryWorker loop breaker closed after {CooldownMinutes} minute(s); recovery resumes")]
  static partial void LogLoopBreakerClosed(ILogger logger, int cooldownMinutes);

  [LoggerMessage(EventId = 6, Level = LogLevel.Information,
    Message = "DeadLetterRecoveryWorker recovered DLQ row {DeadLetterId} ({SourceTable})")]
  static partial void LogRecovered(ILogger logger, Guid deadLetterId, string sourceTable);

  [LoggerMessage(EventId = 7, Level = LogLevel.Information,
    Message = "DeadLetterRecoveryWorker held DLQ row {DeadLetterId} for review (policy={PolicyName})")]
  static partial void LogHeld(ILogger logger, Guid deadLetterId, string policyName);

  [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
    Message = "DeadLetterRecoveryWorker permanently-failed DLQ row {DeadLetterId} (policy={PolicyName})")]
  static partial void LogPermanentlyFailed(ILogger logger, Guid deadLetterId, string policyName);

  [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
    Message = "DeadLetterRecoveryWorker recovery attempt failed for {DeadLetterId}; will reschedule")]
  static partial void LogRecoveryAttemptFailed(ILogger logger, Guid deadLetterId, Exception ex);

  [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
    Message = "DeadLetterRecoveryWorker failed to set terminal state for {DeadLetterId}")]
  static partial void LogTerminalSetFailed(ILogger logger, Guid deadLetterId, Exception ex);

  [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
    Message = "DeadLetterRecoveryWorker failed to schedule next attempt for {DeadLetterId}")]
  static partial void LogScheduleFailed(ILogger logger, Guid deadLetterId, Exception ex);
}
