using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// Stream-integrity Phase B: the origin-side checkpoint publisher. Every
/// <see cref="StreamIntegrityOptions.CheckpointIntervalSeconds"/> it advances the checkpoint
/// watermark through the coordinator (a multi-instance-safe compare-and-swap — one winner per
/// window) and publishes one <see cref="IntegrityCheckpoint"/> through the normal dispatch
/// pipeline. Checkpoints publish even when the window is EMPTY: a missing checkpoint is the
/// consumer's liveness alarm, so silence must always be abnormal. ON by default; disable with
/// <see cref="StreamIntegrityOptions.CheckpointsEnabled"/>.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityCheckpointWorkerTests.cs</tests>
public sealed partial class IntegrityCheckpointWorker(
  IServiceScopeFactory scopeFactory,
  ISchemaReadyGate schemaReadyGate,
  IOptions<StreamIntegrityOptions> options,
  ILogger<IntegrityCheckpointWorker> logger) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<IntegrityCheckpointWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.CheckpointsEnabled) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
      return;
    }
    LogStarted(_logger, _options.CheckpointIntervalSeconds);

    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
    } catch (OperationCanceledException) {
      return;
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await RunCheckpointOnceAsync(stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogError(_logger, ex);
      }

      try {
        await Task.Delay(TimeSpan.FromSeconds(_options.CheckpointIntervalSeconds), stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }
  }

  /// <summary>One checkpoint cycle: advance the watermark, publish the window (empty included).</summary>
  public async Task RunCheckpointOnceAsync(CancellationToken cancellationToken) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
    var dispatcher = scope.ServiceProvider.GetService<IDispatcher>();
    if (coordinator is null || dispatcher is null) {
      return;   // schema-only / diagnostic hosts: nothing to checkpoint with.
    }

    var window = await coordinator.AdvanceIntegrityCheckpointAsync(cancellationToken).ConfigureAwait(false);
    if (window is null) {
      // Unsupported engine, or another instance won this window's advance — nothing to publish.
      return;
    }

    var originServiceId = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
    var originServiceName = scope.ServiceProvider.GetService<IServiceInstanceProvider>()?.ServiceName ?? string.Empty;

    var checkpoint = new IntegrityCheckpoint {
      CheckpointStreamId = originServiceId,
      OriginServiceId = originServiceId,
      OriginServiceName = originServiceName,
      FromCommitSequence = window.FromCommitSequence,
      ToCommitSequence = window.ToCommitSequence,
      Buckets = [.. window.Buckets],
    };

    // ROUTING: the checkpoint rides the ORIGIN'S OWN event topics — the topics its consumers
    // already subscribe to. Namespace-routing it (the dispatcher default) sends it to a
    // control-plane topic that, in a domain-ownership topology, no consumer subscribes to — the
    // broker silently drops every copy, origin tracking never populates, and the deep audit is
    // permanently inert. One publish per DISTINCT topic across the window's types UNION the
    // historically-emitted (own-lane digest) types, so quiet periods still heartbeat every
    // covered topic. Falls back to the dispatcher when the transport infrastructure (or any
    // resolvable topic) is absent — in-memory hosts keep the original behavior.
    var topicsPublished = await _tryPublishToOwnEventTopicsAsync(
      scope.ServiceProvider, coordinator, checkpoint, window, cancellationToken).ConfigureAwait(false);
    if (topicsPublished == 0) {
      await dispatcher.PublishAsync(checkpoint).ConfigureAwait(false);
    }
    LogPublished(_logger, window.FromCommitSequence, window.ToCommitSequence, window.Buckets.Count);
    scope.ServiceProvider.GetService<Observability.StreamIntegrityMetrics>()?.CheckpointsPublished.Add(1);

    // Consumer-side liveness (every service is both origin and consumer): an origin whose
    // checkpoints stopped arriving is itself an integrity signal — 3× the interval by convention.
    var tracker = scope.ServiceProvider.GetService<IntegrityGapTracker>();
    if (tracker is not null) {
      var staleAfter = TimeSpan.FromSeconds(_options.CheckpointIntervalSeconds * 3L);
      foreach (var (staleOriginId, staleOriginName, lastSeen) in tracker.GetStaleOrigins(staleAfter, DateTimeOffset.UtcNow)) {
        LogStaleOrigin(_logger, staleOriginName, staleOriginId, lastSeen);
      }
    }
  }

  /// <summary>
  /// Publishes the checkpoint envelope once per DISTINCT topic of this origin's audited event
  /// types. Returns the number of topics published to; 0 means the caller should use the
  /// dispatcher fallback (missing transport infrastructure, or no type resolved to a topic).
  /// </summary>
  private async Task<int> _tryPublishToOwnEventTopicsAsync(
      IServiceProvider services, IWorkCoordinator coordinator, IntegrityCheckpoint checkpoint,
      IntegrityCheckpointWindow window, CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var routing = services.GetService<IOutboxRoutingStrategy>();
    var catalog = services.GetService<IMessageTypeCatalog>();
    if (transport is null || serializer is null || routing is null || catalog is null) {
      return 0;
    }

    var typeNames = new HashSet<string>(StringComparer.Ordinal);
    foreach (var bucket in window.Buckets) {
      typeNames.Add(bucket.EventType);
    }
    foreach (var emitted in await coordinator.GetOwnAuditedEventTypesAsync(cancellationToken).ConfigureAwait(false)) {
      typeNames.Add(emitted);
    }
    if (typeNames.Count == 0) {
      return 0;
    }

    var lookup = EventTypeMatchingHelper.BuildTypeLookup(
      [.. catalog.GetAll().Where(e => e.Kind == "event").Select(e => e.Type)]);
    var ownedDomains = services.GetService<IOptions<RoutingOptions>>()?.Value
      .OwnedDomains?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
    var destinations = new Dictionary<string, TransportDestination>(StringComparer.Ordinal);
    foreach (var typeName in typeNames) {
      if (EventTypeMatchingHelper.TryResolveType(lookup, typeName, out var eventType)) {
        var destination = routing.GetDestination(eventType, ownedDomains, MessageKind.Event);
        destinations.TryAdd(destination.Address, destination);
      }
    }
    if (destinations.Count == 0) {
      return 0;
    }

    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var envelope = new MessageEnvelope<IntegrityCheckpoint> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = checkpoint,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    foreach (var destination in destinations.Values) {
      await transport.PublishAsync(serialized.JsonEnvelope, destination, serialized.EnvelopeType,
        cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    LogPublishedToTopics(_logger, destinations.Count, typeNames.Count);
    return destinations.Count;
  }

  [LoggerMessage(EventId = 60, Level = LogLevel.Information,
    Message = "Integrity checkpoint publisher started (interval {IntervalSeconds}s)")]
  static partial void LogStarted(ILogger logger, int intervalSeconds);

  [LoggerMessage(EventId = 61, Level = LogLevel.Information,
    Message = "Integrity checkpoints disabled by configuration")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 62, Level = LogLevel.Debug,
    Message = "Integrity checkpoint published for window ({FromCommitSequence}, {ToCommitSequence}] with {BucketCount} bucket(s)")]
  static partial void LogPublished(ILogger logger, long fromCommitSequence, long toCommitSequence, int bucketCount);

  [LoggerMessage(EventId = 63, Level = LogLevel.Error,
    Message = "Integrity checkpoint cycle failed; will retry next interval")]
  static partial void LogError(ILogger logger, Exception exception);

  [LoggerMessage(EventId = 65, Level = LogLevel.Debug,
    Message = "Integrity checkpoint published to {TopicCount} own event topic(s) covering {TypeCount} audited type(s)")]
  static partial void LogPublishedToTopics(ILogger logger, int topicCount, int typeCount);

  [LoggerMessage(EventId = 64, Level = LogLevel.Warning,
    Message = "Origin '{OriginServiceName}' ({OriginServiceId}) has not checkpointed since {LastSeen:o} — " +
              "its continuity can no longer be verified (liveness alarm)")]
  static partial void LogStaleOrigin(ILogger logger, string originServiceName, Guid originServiceId, DateTimeOffset lastSeen);
}
