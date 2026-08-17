using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// The paced repair drain: dispatches stream-integrity repair requests from the durable ledger
/// at a steady, token-bucket-paced rate — instead of the audit compare bursting its whole
/// budget at each tick. Discovery (audits, checkpoints) records deficits and their compared
/// windows; this worker CLAIMS eligible rows (past backoff, under the attempt cap,
/// least-recently-attempted first, only origins with a learned request topic) and sends one
/// directed, range-bounded <see cref="RequestRedeliveryCommand"/> per (origin, tenant, type)
/// group. Inert without a drain-capable coordinator, a transport, or learned origins.
/// </summary>
/// <docs>proposals/paced-repair-drain</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/RepairDrainWorkerTests.cs</tests>
public sealed partial class RepairDrainWorker(
    IServiceScopeFactory scopeFactory,
    ISchemaReadyGate schemaReadyGate,
    IOptions<StreamIntegrityOptions> options,
    ILogger<RepairDrainWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<RepairDrainWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
  private double _tokens;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.RepairDrainEnabled || _options.RepairDrainRatePerSecond <= 0) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.InfiniteTimeSpan, _time, stoppingToken); } catch (OperationCanceledException) { }
      return;
    }
    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
    } catch (OperationCanceledException) {
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
  /// and dispatch them grouped per (origin, tenant, type). Each claimed ROW costs one token
  /// (grouping only ever sends fewer wire requests than rows paid for). Internal for
  /// deterministic tests; the loop above supplies real elapsed time.
  /// </summary>
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
    var coordinator = services.GetService<IWorkCoordinator>();
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var tracker = services.GetService<IntegrityGapTracker>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var replyTopic = _options.RepairTopic
      ?? services.GetService<TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
    if (coordinator is null || transport is null || serializer is null || tracker is null
        || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(replyTopic)) {
      return;   // no dispatch infrastructure — the ledger keeps the backlog durable.
    }

    // Directed or not at all — the same rule as every other integrity send: only origins whose
    // request topic a checkpoint has taught. Unlearned origins are not CLAIMED, so no attempt
    // budget is ever burned on a request that could not leave the process.
    var origins = tracker.GetOrigins().Where(o => !string.IsNullOrEmpty(o.RequestTopic)).ToList();
    if (origins.Count == 0) {
      return;
    }

    var budget = (int)Math.Min(Math.Floor(_tokens), _options.RepairDrainBatchSize);
    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      origins.Select(o => o.OriginServiceId).ToList(), now,
      TimeSpan.FromSeconds(_options.RepairRequestBackoffSeconds), _options.MaxRepairAttemptsPerBucket,
      budget, cancellationToken).ConfigureAwait(false);
    if (claimed.Count == 0) {
      return;
    }
    _tokens -= claimed.Count;

    var metrics = services.GetService<StreamIntegrityMetrics>();
    var byOrigin = origins.ToDictionary(o => o.OriginServiceId, o => (o.OriginServiceName, o.RequestTopic));
    foreach (var group in claimed.GroupBy(c => (c.OriginServiceId, c.TenantScope, c.EventType))) {
      if (!byOrigin.TryGetValue(group.Key.OriginServiceId, out var origin)) {
        continue;   // learned set changed mid-pass; the attempt re-offers after backoff.
      }
      var items = group.ToList();
      // Any pre-stamp row (null window) widens the ask to whole history — the legacy semantics,
      // correct just coarser. Otherwise the group's union range, converted exactly as the burst
      // path converted the manifest window: exclusive floor, inclusive ceiling.
      var anyUnwindowed = items.Exists(i => i.WindowFrom is null || i.WindowUntil is null);
      long? fromSeq = null, toSeq = null;
      if (!anyUnwindowed) {
        var from = items.Min(i => i.WindowFrom!.Value);
        var until = items.Max(i => i.WindowUntil!.Value);
        fromSeq = from > 0 ? from - 1 : null;
        toSeq = until - 1;
      }
      var envelope = new MessageEnvelope<RequestRedeliveryCommand> {
        MessageId = new MessageId(TrackedGuid.NewMedo()),
        Payload = new RequestRedeliveryCommand {
          TenantScope = string.IsNullOrEmpty(group.Key.TenantScope) ? null : group.Key.TenantScope,
          EventTypes = [group.Key.EventType],
          StreamIds = items.ConvertAll(i => i.StreamId),
          RequesterService = requester,
          Topic = replyTopic,
          FromCommitSequence = fromSeq,
          ToCommitSequence = toSeq,
        },
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = now,
            ServiceInstance = instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown
          }
        ],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
        Target = origin.OriginServiceName,
      };
      var serialized = serializer.SerializeEnvelope(envelope);
      try {
        await transport.PublishAsync(serialized.JsonEnvelope,
          ControlPlaneDestination.For(origin.RequestTopic!, items[0].StreamId, typeof(RequestRedeliveryCommand)),
          serialized.EnvelopeType, cancellationToken: cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        // A transient broker failure costs THIS group's attempt (already stamped on claim) — it
        // must never kill the remaining groups in the tick; their rows also burned an attempt
        // and deserve their shot at the wire. The ladder re-offers this group after backoff.
        LogGroupDispatchFailed(_logger, group.Key.EventType, origin.OriginServiceName, ex);
        continue;
      }
      metrics?.RepairsRequested.Add(items.Count,
        new KeyValuePair<string, object?>("source", "drain"),
        new KeyValuePair<string, object?>("origin", origin.OriginServiceName));
      LogDispatched(_logger, items.Count, group.Key.EventType, origin.OriginServiceName);
    }
  }

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
    Message = "DRAIN dispatch of {EventType} to '{OriginServiceName}' failed; the group's attempt is spent and the ladder re-offers it after backoff")]
  private static partial void LogGroupDispatchFailed(ILogger logger, string eventType, string originServiceName, Exception exception);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "DRAIN dispatched {Count} bucket(s) of {EventType} to '{OriginServiceName}'")]
  private static partial void LogDispatched(ILogger logger, int count, string eventType, string originServiceName);
}
