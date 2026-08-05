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
/// consumer subscribes to are verified; a service's own checkpoints are ignored (locally
/// originated events carry no origin stamp to count against).
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCheckpointReceptorTests.cs</tests>
public sealed partial class IntegrityCheckpointReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrityCheckpointReceptor> logger) : IReceptor<IntegrityCheckpoint> {

  /// <inheritdoc />
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

    var self = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
    if (message.OriginServiceId == self) {
      // Own checkpoint: locally-originated events persist NO origin stamp, so a self-count would
      // always read zero and every bucket would false-alarm.
      return;
    }

    tracker.RecordCheckpoint(message.OriginServiceId, message.OriginServiceName, DateTimeOffset.UtcNow, message.RequestTopic);
    var metrics = services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
    metrics?.CheckpointsReceived.Add(1, new KeyValuePair<string, object?>("origin", message.OriginServiceName));
    var repairBudget = options.MaxAutoRepairRequestsPerCheckpoint;

    // 1) Two-cycle confirmation: deficits recorded on the PREVIOUS checkpoint, recounted now.
    foreach (var pending in tracker.TakePending(message.OriginServiceId)) {
      var recount = await coordinator
        .CountReceivedFromOriginAsync(pending.OriginServiceId, pending.FromCommitSequence, pending.ToCommitSequence, cancellationToken)
        .ConfigureAwait(false);
      var actual = _bucketCount(recount, pending.TenantScope, pending.EventType);
      if (actual >= pending.ExpectedCount) {
        continue;   // healed in flight — the straggler arrived between checkpoints
      }

      var autoRepair = options.RepairMode == IntegrityRepairMode.AutoRepairCapped && repairBudget > 0;
      metrics?.GapsDetected.Add(1,
        new KeyValuePair<string, object?>("origin", pending.OriginServiceName),
        new KeyValuePair<string, object?>("event_type", pending.EventType));
      if (autoRepair) {
        repairBudget--;
        metrics?.RepairsRequested.Add(1,
          new KeyValuePair<string, object?>("source", "checkpoint"),
          new KeyValuePair<string, object?>("origin", pending.OriginServiceName));
        await _sendRepairRequestAsync(services, options, pending, cancellationToken).ConfigureAwait(false);
      }
      await dispatcher.PublishAsync(new IntegrityGapDetected {
        ReportStreamId = TrackedGuid.NewMedo().Value,
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
      LogGapConfirmed(logger, pending.EventType, pending.TenantScope, pending.OriginServiceName,
        pending.FromCommitSequence, pending.ToCommitSequence, pending.ExpectedCount, actual, autoRepair);
    }

    // 2) Evaluate THIS window: deficits become pendings the NEXT checkpoint confirms or clears.
    var subscribed = _subscribedTypeNames(services);
    if (subscribed.Count == 0) {
      return;
    }
    var actualBuckets = await coordinator
      .CountReceivedFromOriginAsync(message.OriginServiceId, message.FromCommitSequence, message.ToCommitSequence, cancellationToken)
      .ConfigureAwait(false);
    foreach (var bucket in message.Buckets) {
      if (!subscribed.Contains(bucket.EventType)) {
        continue;   // not consumed here — the origin's count is someone else's contract
      }
      var actual = _bucketCount(actualBuckets, bucket.TenantScope, bucket.EventType);
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
  }

  /// <summary>Wire-only, directed at the origin — the pump's own publish pattern. A lost request
  /// re-fires on the next confirmation cycle, so no outbox durability is needed.</summary>
  private async Task _sendRepairRequestAsync(
      IServiceProvider services, StreamIntegrityOptions options,
      IntegrityGapTracker.PendingGap pending, CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var requester = services.GetService<IServiceInstanceProvider>()?.ServiceName;
    var topic = options.RepairTopic
      ?? services.GetService<TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
    if (transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      LogRepairSkipped(logger, pending.OriginServiceName,
        transport is null, serializer is null, string.IsNullOrEmpty(requester), string.IsNullOrEmpty(topic));
      return;
    }

    var envelope = new MessageEnvelope<RequestRedeliveryCommand> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new RequestRedeliveryCommand {
        TenantScope = pending.TenantScope,
        EventTypes = [pending.EventType],
        FromCommitSequence = pending.FromCommitSequence,
        ToCommitSequence = pending.ToCommitSequence,
        RequesterService = requester,
        Topic = topic,
      },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = services.GetService<IServiceInstanceProvider>()?.ToInfo() ?? ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = pending.OriginServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    // Publish the DIRECTED redelivery request to the ORIGIN-carried address (session-stamped);
    // the requester's own topic is only the reply address inside the payload.
    var originRequestTopic = services.GetService<IntegrityGapTracker>()?.GetRequestTopic(pending.OriginServiceId);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.ControlPlaneDestination.For(originRequestTopic ?? topic, envelope.MessageId.Value), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
  }

  private static int _bucketCount(IReadOnlyList<CheckpointBucket> buckets, string? tenantScope, string eventType) =>
    buckets.Where(b => b.EventType == eventType && b.TenantScope == tenantScope).Sum(b => b.Count);

  private static HashSet<string> _subscribedTypeNames(IServiceProvider services) {
    var provider = services.GetService<IEventTypeProvider>();
    if (provider is null) {
      return [];
    }
    return [.. provider.GetEventTypes().Select(TypeNameFormatter.FormatClrTypeName)];
  }

  [LoggerMessage(EventId = 50, Level = LogLevel.Warning,
    Message = "CONFIRMED integrity gap: {EventType} (tenant {TenantScope}) from origin '{OriginServiceName}' " +
              "window ({FromCommitSequence}, {ToCommitSequence}] — expected {ExpectedCount}, have {ActualCount} " +
              "(autoRepair={AutoRepairRequested})")]
  static partial void LogGapConfirmed(ILogger logger, string eventType, string? tenantScope, string originServiceName,
    long fromCommitSequence, long toCommitSequence, int expectedCount, int actualCount, bool autoRepairRequested);

  [LoggerMessage(EventId = 51, Level = LogLevel.Warning,
    Message = "Auto-repair request to '{OriginServiceName}' skipped — missing infrastructure " +
              "(transport={TransportMissing}, serializer={SerializerMissing}, requester={RequesterMissing}, topic={TopicMissing})")]
  static partial void LogRepairSkipped(ILogger logger, string originServiceName,
    bool transportMissing, bool serializerMissing, bool requesterMissing, bool topicMissing);
}
