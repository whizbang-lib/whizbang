// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// The paced repair drain: dispatches stream-integrity repair requests from the durable ledger
/// at a steady, token-bucket-paced rate — instead of the audit compare bursting its whole
/// budget at each tick. Discovery (audits, checkpoints) records deficits and their compared
/// windows; this worker CLAIMS eligible rows (past backoff, under the attempt cap,
/// least-recently-attempted first, only origins with a learned request topic) and sends one
/// directed, range-bounded <see cref="RequestRedeliveryCommand"/> per (origin, tenant, type, window)
/// group. Each group's buckets are recorded as awaiting their answer before the request is sent, so they are not claimed
/// again while the answer is on its way (#2029) and an answer that outruns the send finds them waiting; a send that fails
/// takes the record back and returns the claim's attempt (#2223). Inert without a drain-capable coordinator, a transport, or learned origins. Runs on the instance holding the
/// maintainer duty, and only once the housekeeping idle gate admits integrity work (forced through after
/// <c>Whizbang:Housekeeping:MaxDeferral</c>); tokens keep accruing, up to the burst cap, while it waits (#2001). It claims
/// nothing while <see cref="StreamIntegrityOptions.MaxOutstandingRepairAnswers"/> buckets await their answer, and never
/// more than the room left under that cap (#2184).
/// </summary>
/// <docs>proposals/paced-repair-drain</docs>
/// <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
/// <docs>resilience/stream-integrity#outstanding-repair-answers-are-capped</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/RepairDrainWorkerTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RepairDrainRecordsBeforeSendingTests.cs</tests>
public sealed partial class RepairDrainWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StreamIntegrityOptions> options,
    ILogger<RepairDrainWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<RepairDrainWorker> _logger = ArgumentGuard.NotNull(logger);
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
  private double _tokens;
  private bool _heldByAnswerCap;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.RepairDrainEnabled || _options.RepairDrainRatePerSecond <= 0) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.InfiniteTimeSpan, _time, stoppingToken); } catch (OperationCanceledException) { /* stopping is the normal way out of this wait */ }
      return;
    }
    LogStarted(_logger, _options.RepairDrainRatePerSecond);
    var last = _time.GetUtcNow();
    while (!stoppingToken.IsCancellationRequested) {
      try {
        await Task.Delay(TimeSpan.FromSeconds(1), _time, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
      var now = _time.GetUtcNow();
      var elapsed = (now - last).TotalSeconds;
      last = now;
      try {
        await DrainTickAsync(elapsed, now, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogTickFailed(_logger, ex);
      }
    }
  }

  /// <summary>
  /// One pacing tick: refill tokens at the configured rate (burst cap 2×), then — if at least
  /// one whole token is available — claim up to min(⌊tokens⌋, batch size) eligible ledger rows
  /// and dispatch them grouped per (origin, tenant, type, window). Each claimed ROW costs one token
  /// (grouping only ever sends fewer wire requests than rows paid for). Internal for
  /// deterministic tests; the loop above supplies real elapsed time.
  /// </summary>
  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "One pacing tick refills the token bucket and then, only if a whole token is available and every collaborator is wired, claims ledger rows, records each group as awaiting its answer and dispatches it, grouped per origin, tenant, type and window, taking the record back when its send fails. The early exits are the pacing.")]
  internal async Task DrainTickAsync(double elapsedSeconds, DateTimeOffset now, CancellationToken cancellationToken) {
    if (_options.RepairMode != IntegrityRepairMode.AutoRepairCapped) {
      // ReportOnly is the operator's explicit opt-DOWN from auto-repair, and the drain is a
      // repair dispatcher — it must not claim (a claim stamps an attempt) or send anything.
      return;
    }
    var rate = _options.RepairDrainRatePerSecond;
    _tokens = Math.Min(_tokens + (rate * Math.Max(0, elapsedSeconds)), rate * 2);
    if (_tokens < 1) {
      return;
    }

    await using var scope = _scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    if (!MaintainerDuty.IsThisInstancesTurn(services)) {
      return;   // #2001: another instance holds the maintainer duty and drains the ledger.
    }
    var coordinator = services.GetService<IWorkCoordinator>();
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var tracker = services.GetService<IntegrityGapTracker>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var replyTopic = RepairTopicResolver.Resolve(_options.RepairTopic, services);
    if (coordinator is null || transport is null || serializer is null || tracker is null
        || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(replyTopic)) {
      return;   // no dispatch infrastructure — the ledger keeps the backlog durable.
    }

    // Directed or not at all — the same rule as every other integrity send: only origins whose
    // request topic a checkpoint has taught. Unlearned origins are not CLAIMED, so no attempt
    // budget is ever burned on a request that could not leave the process.
    // #2104: the origins the store kept count from the first tick after a restart.
    await tracker.EnsureHydratedAsync(coordinator, cancellationToken).ConfigureAwait(false);
    var origins = tracker.GetOrigins().Where(o => !string.IsNullOrEmpty(o.RequestTopic)).ToList();
    if (origins.Count == 0) {
      return;
    }
    // #2001: asked only once there is something to send, so an idle drain does not measure the backlog every second.
    await using var admission = await IntegrityAdmission.TryAdmitAsync(
      services, coordinator, nameof(RepairDrainWorker), _logger, cancellationToken).ConfigureAwait(false);
    if (!admission.Granted) {
      return;
    }

    var budget = (int)Math.Min(Math.Floor(_tokens), _options.RepairDrainBatchSize);
    // #2184: answers land up to the answer timeout after their requests, so the rate alone does not bound the load they
    // put on this store. While the cap's worth of buckets await an answer, nothing new is claimed.
    var cap = _options.MaxOutstandingRepairAnswers;
    var metrics = services.GetService<StreamIntegrityMetrics>();
    if (cap > 0) {
      var pending = await coordinator.IntegrityCountAnswerPendingAsync(now, cap, cancellationToken).ConfigureAwait(false);
      if (pending >= cap) {
        if (!_heldByAnswerCap) {
          _heldByAnswerCap = true;
          metrics?.RepairDrainHolds.Add(1);
          LogHeldByAnswerCap(_logger, pending, cap);
        }
        return;
      }
      if (_heldByAnswerCap) {
        _heldByAnswerCap = false;
        LogResumedUnderAnswerCap(_logger, pending, cap);
      }
      budget = Math.Min(budget, cap - pending);
    }
    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [.. origins.Select(o => o.OriginServiceId)], now,
      TimeSpan.FromSeconds(_options.RepairRequestBackoffSeconds), _options.MaxRepairAttemptsPerBucket,
      budget, cancellationToken).ConfigureAwait(false);
    if (claimed.Count == 0) {
      return;
    }
    _tokens -= claimed.Count;

    var byOrigin = origins.ToDictionary(o => o.OriginServiceId, o => (o.OriginServiceName, o.RequestTopic));
    var answerTimeout = TimeSpan.FromMinutes(_options.RepairAnswerTimeoutMinutes);
    // #2034: grouped by window too, so no stream is re-shipped over a wider range than its own divergence. Rows a
    // comparison stamped together share their window, so this costs few extra requests.
    // #2103: rows that name their one missing event ask for those events, apart from rows that ask for their streams:
    // an id list narrows a request to its ids.
    foreach (var group in claimed.GroupBy(c => (c.OriginServiceId, c.TenantScope, c.EventType, c.WindowFrom, c.WindowUntil,
                                                Named: c.MissingEventId is not null))) {
      if (!byOrigin.TryGetValue(group.Key.OriginServiceId, out var origin)) {
        continue;   // learned set changed mid-pass; the attempt re-offers after backoff.
      }
      var items = group.ToList();
      // A pre-stamp row (null window) asks for whole history, the legacy semantics, correct just coarser. Otherwise
      // the window converts exactly as the burst path converted the manifest window: exclusive floor, inclusive ceiling.
      long? fromSeq = null, toSeq = null;
      if (group.Key.WindowFrom is long from && group.Key.WindowUntil is long until) {
        fromSeq = from > 0 ? from - 1 : null;
        toSeq = until - 1;
      }
      var command = new RequestRedeliveryCommand {
        TenantScope = string.IsNullOrEmpty(group.Key.TenantScope) ? null : group.Key.TenantScope,
        EventTypes = [group.Key.EventType],
        StreamIds = items.ConvertAll(i => i.StreamId),
        EventIds = group.Key.Named ? items.ConvertAll(i => i.MissingEventId!.Value) : null,
        RequesterService = requester,
        Topic = replyTopic,
        FromCommitSequence = fromSeq,
        ToCommitSequence = toSeq,
        AcceptsStreamSpanningBundles = true,
      };
      // #2029, #2223: recorded as awaiting its answer before it is sent. An answer clears the wait, and the origin can
      // answer before the publish call returns (a fast origin, or this host's continuation delayed under load); recorded
      // after, the late record put the wait back over that answer and the bucket counted against the cap until the
      // answer timeout.
      var keys = items.ConvertAll(i => new IntegrityRepairLedger.DivergenceKey(i.OriginServiceId, i.TenantScope, i.EventType, i.StreamId));
      await coordinator.IntegrityMarkAnswerPendingAsync(group.Key.OriginServiceId, keys, null, null, now, answerTimeout, cancellationToken)
        .ConfigureAwait(false);
      try {
        await RedeliveryRequestDispatch.PublishAsync(
          transport, serializer, instanceProvider, command, origin.OriginServiceName,
          IntegrityControlDestination.For(origin.RequestTopic!, items[0].StreamId, typeof(RequestRedeliveryCommand), _options),
          now, cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        // A transient broker failure never kills the remaining groups in the tick: their rows were claimed too and
        // deserve their shot at the wire. This group's request never left, so its record is taken back and the claim's
        // attempt returned (#2223); the ladder re-offers it after backoff.
        LogGroupDispatchFailed(_logger, group.Key.EventType, origin.OriginServiceName, ex);
        await coordinator.IntegrityWithdrawAnswerPendingAsync(group.Key.OriginServiceId, keys, now, answerTimeout, cancellationToken)
          .ConfigureAwait(false);
        continue;
      }
      metrics?.RepairsRequested.Add(items.Count,
        new KeyValuePair<string, object?>("source", "drain"),
        new KeyValuePair<string, object?>("origin", origin.OriginServiceName));
      LogDispatched(_logger, items.Count, group.Key.EventType, origin.OriginServiceName);
    }
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Paced repair drain holding: {Pending} repair-ledger bucket(s) await their answer, at the cap of {Cap} "
            + "(Whizbang:StreamIntegrity:MaxOutstandingRepairAnswers); no new repair is requested until answers land or time out")]
  private static partial void LogHeldByAnswerCap(ILogger logger, int pending, int cap);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Paced repair drain resumed: {Pending} repair-ledger bucket(s) await their answer, under the cap of {Cap}")]
  private static partial void LogResumedUnderAnswerCap(ILogger logger, int pending, int cap);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Paced repair drain disabled — repair dispatch falls to whatever discovery-side path remains")]
  private static partial void LogDisabled(ILogger logger);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Paced repair drain started at {RatePerSecond} row(s)/s")]
  private static partial void LogStarted(ILogger logger, double ratePerSecond);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Repair drain tick failed; the ledger keeps the backlog and the next tick retries")]
  private static partial void LogTickFailed(ILogger logger, Exception exception);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "DRAIN dispatch of {EventType} to '{OriginServiceName}' failed; the request is withdrawn, its attempt returned, and the ladder re-offers it after backoff")]
  private static partial void LogGroupDispatchFailed(ILogger logger, string eventType, string originServiceName, Exception exception);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "DRAIN dispatched {Count} bucket(s) of {EventType} to '{OriginServiceName}'")]
  private static partial void LogDispatched(ILogger logger, int count, string eventType, string originServiceName);
}
