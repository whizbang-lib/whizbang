// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.DependencyInjection;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Startup;
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
/// <para>
/// Runs on the instance holding the maintainer duty (#2001), like the rest of integrity work, but is deliberately NOT
/// idle-gated: it never asks the housekeeping coordinator for the integrity slot. A consumer reads a missing checkpoint
/// as its liveness alarm, so a checkpoint deferred because its origin is busy would make every busy service look
/// dead to its consumers. Publishing one is a watermark advance and a publish, cheap enough to keep its cadence under
/// any load.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/IntegrityCheckpointWorkerTests.cs</tests>
public sealed partial class IntegrityCheckpointWorker(
  IServiceScopeFactory scopeFactory,
  IOptions<StreamIntegrityOptions> options,
  ILogger<IntegrityCheckpointWorker> logger,
  TimeProvider? timeProvider = null) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<IntegrityCheckpointWorker> _logger = ArgumentGuard.NotNull(logger);
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.CheckpointsEnabled) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { /* stopping is the normal way out of this wait */ }
      return;
    }
    LogStarted(_logger, _options.CheckpointIntervalSeconds);

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
    using var priorityScope = Whizbang.Core.Priority.PriorityContext.Enter(Whizbang.Core.Priority.WorkPriority.BACKGROUND);   // system work nobody waits on: everything dispatched here inherits background
    // #2001: on the maintainer only (every instance when role assignment does not manage that duty; the watermark CAS
    // still picks one winner per window). Never idle-gated: see the type's remarks.
    if (!MaintainerDuty.IsThisInstancesTurn(scope.ServiceProvider)) {
      return;
    }
    var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
    var dispatcher = scope.ServiceProvider.GetService<IDispatcher>();
    if (coordinator is null || dispatcher is null) {
      return;   // schema-only / diagnostic hosts: nothing to checkpoint with.
    }

    // The checkpoint stream is the origin's id, so with no id yet there is nothing to publish on.
    // Checked before the advance: advancing without publishing would lose the window's counts.
    var originServiceId = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
    if (originServiceId == Guid.Empty) {
      LogServiceIdentityUnknown(_logger);
      return;
    }

    var window = await coordinator.AdvanceIntegrityCheckpointAsync(cancellationToken).ConfigureAwait(false);
    if (window is null) {
      // Unsupported engine, or another instance won this window's advance — nothing to publish.
      return;
    }

    var originServiceName = scope.ServiceProvider.GetService<IServiceInstanceProvider>()?.ServiceName ?? string.Empty;
    // #2008: the local lane follows registered ownership. A type this service does not own was received, not authored,
    // so it is not counted as an emission; the owned domains ride the checkpoint so consumers learn who authors what.
    var ownedDomains = _ownedDomains(scope.ServiceProvider);

    var checkpoint = new IntegrityCheckpoint {
      CheckpointStreamId = originServiceId,
      OriginServiceId = originServiceId,
      OriginServiceName = originServiceName,
      // The address consumers publish directed integrity requests to — a topic THIS service
      // consumes (its own first destination, or the RepairTopic override). Carried because the
      // requester cannot guess an origin-reachable topic in a domain-scoped topology.
      RequestTopic = RepairTopicResolver.Resolve(_options.RepairTopic, scope.ServiceProvider),
      FromCommitSequence = window.FromCommitSequence,
      ToCommitSequence = window.ToCommitSequence,
      Buckets = [.. window.Buckets.Where(b => OwnedNamespaceMatcher.IsLocalEventType(b.EventType, ownedDomains))],
      OwnedDomains = [.. ownedDomains.Select(OwnedNamespaceMatcher.NormalizeDomain).Distinct(StringComparer.OrdinalIgnoreCase)],
    };

    // ROUTING: the checkpoint rides the ORIGIN'S OWN event topics — the topics its consumers
    // already subscribe to. Namespace-routing it (the dispatcher default) sends it to a
    // control-plane topic that, in a domain-ownership topology, no consumer subscribes to — the
    // broker silently drops every copy, origin tracking never populates, and the deep audit is
    // permanently inert. One publish per DISTINCT topic of the types this service owns (#2031: other services' types
    // would land a copy on every topic a consumer subscribes to); with no owned domains registered, the window's types
    // UNION the historically-emitted (own-lane digest) types, so quiet periods still heartbeat every covered topic. Falls back to the dispatcher when the transport infrastructure (or any
    // resolvable topic) is absent — in-memory hosts keep the original behavior.
    // CONTROL CLASS (topology arc phase 9): the checkpoint is the archetypal supersedable control
    // signal — the next cycle re-derives it from the same watermarks, so a copy that outlives its
    // successor is pure backlog. mint.Checkpoints owns the lifetime derivation (TTL ≈ 2× cadence) —
    // the worker supplies only the cadence it actually runs on, so retuning the interval retunes
    // the lifetime with it. Absent mint (host built without AddWhizbang) ⇒ no TTL, pre-phase-9.
    var minted = scope.ServiceProvider.GetService<Minting.ICheckpointMint>()?.Mint(
      new Minting.ControlMintRequest<IntegrityCheckpoint> {
        Payload = checkpoint,
        Cadence = TimeSpan.FromSeconds(_options.CheckpointIntervalSeconds),
      });

    var topicsPublished = await _tryPublishToOwnEventTopicsAsync(
      scope.ServiceProvider, coordinator, checkpoint, ownedDomains, minted?.TimeToLive, cancellationToken).ConfigureAwait(false);
    if (topicsPublished == 0) {
      await dispatcher.PublishAsync(checkpoint).ConfigureAwait(false);
    }
    LogPublished(_logger, window.FromCommitSequence, window.ToCommitSequence, window.Buckets.Count);
    scope.ServiceProvider.GetService<Observability.StreamIntegrityMetrics>()?.CheckpointsPublished.Add(1);

    // Consumer-side liveness (every service is both origin and consumer): an origin whose
    // checkpoints stopped arriving is itself an integrity signal — 3× the interval by convention.
    var tracker = scope.ServiceProvider.GetService<IntegrityGapTracker>();
    if (tracker is not null) {
      var now = _time.GetUtcNow();
      var staleAfter = TimeSpan.FromSeconds(_options.CheckpointIntervalSeconds * 3L);
      // #2001: received checkpoints wait in the integrity delivery queue while the idle gate defers the drain, so a
      // live origin's checkpoint is late by as long as the deferral. Widen the bound by it rather than raise the alarm.
      if (scope.ServiceProvider.GetService<IntegrityDeliveryWorker>()?.DeferredSince is { } deferredSince) {
        staleAfter += now - deferredSince;
      }
      foreach (var (staleOriginId, staleOriginName, lastSeen) in tracker.GetStaleOrigins(staleAfter, now)) {
        LogStaleOrigin(_logger, staleOriginName, staleOriginId, lastSeen);
      }
    }
  }

  /// <summary>
  /// Stamps a minted control-class lifetime onto a destination, if one was minted. Applied AFTER
  /// <c>ControlPlaneDestination</c> (which replaces the metadata bag wholesale) so the session key
  /// survives — a sessionless delivery to a session-enabled subscription is dead-lettered silently.
  /// </summary>
  private static TransportDestination _withControlLifetime(
      TransportDestination destination, TimeSpan? timeToLive) =>
    timeToLive is { } lifetime && lifetime > TimeSpan.Zero
      ? ControlMessageTtl.Stamp(destination, lifetime)
      : destination;

  /// <summary>
  /// Publishes the checkpoint envelope once per DISTINCT topic of this origin's owned (or, with no owned domains,
  /// audited) event types. Returns the number of topics published to; 0 means the caller should use the
  /// dispatcher fallback (missing transport infrastructure, or no type resolved to a topic).
  /// </summary>
  private async Task<int> _tryPublishToOwnEventTopicsAsync(
      IServiceProvider services, IWorkCoordinator coordinator, IntegrityCheckpoint checkpoint,
      HashSet<string> ownedDomains, TimeSpan? timeToLive, CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var catalog = services.GetService<IMessageTypeCatalog>();
    // Destination resolution mirrors the dispatcher's two layers: the outbox routing strategy
    // when registered, else the generated topic registry (+ optional topic routing strategy).
    var routing = services.GetService<IOutboxRoutingStrategy>();
    var topicRegistry = services.GetService<ITopicRegistry>();
    if (transport is null || serializer is null || catalog is null
        || (routing is null && topicRegistry is null)) {
      return 0;
    }

    var eventTypes = await _checkpointEventTypesAsync(coordinator, catalog, checkpoint, ownedDomains, cancellationToken)
      .ConfigureAwait(false);
    if (eventTypes.Count == 0) {
      return 0;
    }

    var topicStrategy = services.GetService<ITopicRoutingStrategy>();
    var destinations = new Dictionary<string, TransportDestination>(StringComparer.Ordinal);
    foreach (var eventType in eventTypes) {
      // Session-enabled subscriptions dead-letter sessionless deliveries — the checkpoint's own
      // stream identity (one homogeneous checkpoint stream per origin) is the session key.
      if (routing is not null) {
        var destination = _withControlLifetime(
          ControlPlaneDestination.WithSession(
            routing.GetDestination(eventType, ownedDomains, MessageKind.Event), checkpoint.CheckpointStreamId),
          timeToLive);
        destinations.TryAdd(destination.Address, destination);
        continue;
      }
      var baseTopic = topicRegistry!.GetBaseTopic(eventType);
      if (baseTopic is null) {
        continue;   // not a registry-known event — no topic to ride.
      }
      var address = topicStrategy?.ResolveTopic(eventType, baseTopic) ?? baseTopic;
      destinations.TryAdd(address, _withControlLifetime(
        ControlPlaneDestination.For(address, checkpoint.CheckpointStreamId), timeToLive));
    }
    if (destinations.Count == 0) {
      return 0;
    }

    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var envelope = new MessageEnvelope<IntegrityCheckpoint> {
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
      Payload = checkpoint,
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(IntegrityCheckpoint), instanceProvider, DateTimeOffset.UtcNow)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    foreach (var destination in destinations.Values) {
      await transport.PublishAsync(serialized.JsonEnvelope, destination, serialized.EnvelopeType,
        cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    LogPublishedToTopics(_logger, destinations.Count, eventTypes.Count);
    return destinations.Count;
  }

  /// <summary>
  /// The event types whose topics the checkpoint rides. With owned domains registered: every catalog event type the
  /// service owns, so each consumer of an owned topic gets the beat and no consumer gets a copy per topic of another
  /// service's types (#2031). Without: the window's types and the store's own-lane digest types, as before.
  /// </summary>
  private static async Task<List<Type>> _checkpointEventTypesAsync(
      IWorkCoordinator coordinator, IMessageTypeCatalog catalog, IntegrityCheckpoint checkpoint,
      HashSet<string> ownedDomains, CancellationToken cancellationToken) {
    var catalogEvents = catalog.GetAll().Where(e => e.Kind == "event").Select(e => e.Type).ToList();
    if (ownedDomains.Count > 0) {
      return [.. catalogEvents.Where(t => OwnedNamespaceMatcher.IsOwned(t.Namespace, ownedDomains))];
    }

    var typeNames = new HashSet<string>(StringComparer.Ordinal);
    foreach (var bucket in checkpoint.Buckets) {
      typeNames.Add(bucket.EventType);
    }
    foreach (var emitted in await coordinator.GetOwnAuditedEventTypesAsync(cancellationToken).ConfigureAwait(false)) {
      typeNames.Add(emitted);
    }
    var lookup = EventTypeMatchingHelper.BuildTypeLookup(catalogEvents);
    var resolved = new List<Type>();
    foreach (var typeName in typeNames) {
      if (EventTypeMatchingHelper.TryResolveType(lookup, typeName, out var eventType)) {
        resolved.Add(eventType);
      }
    }
    return resolved;
  }

  private static HashSet<string> _ownedDomains(IServiceProvider services) =>
    services.GetOptionsValue<RoutingOptions>()?.OwnedDomains.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

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

  [LoggerMessage(EventId = 66, Level = LogLevel.Debug,
    Message = "Integrity checkpoint skipped: this service's identity is not recorded yet; the window is kept for the next cycle")]
  static partial void LogServiceIdentityUnknown(ILogger logger);

  [LoggerMessage(EventId = 65, Level = LogLevel.Debug,
    Message = "Integrity checkpoint published to {TopicCount} own event topic(s) covering {TypeCount} audited type(s)")]
  static partial void LogPublishedToTopics(ILogger logger, int topicCount, int typeCount);

  [LoggerMessage(EventId = 64, Level = LogLevel.Warning,
    Message = "Origin '{OriginServiceName}' ({OriginServiceId}) has not checkpointed since {LastSeen:o} — " +
              "its continuity can no longer be verified (liveness alarm)")]
  static partial void LogStaleOrigin(ILogger logger, string originServiceName, Guid originServiceId, DateTimeOffset lastSeen);
}
