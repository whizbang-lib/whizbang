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
public sealed partial class IntegrityManifestRequestReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrityManifestRequestReceptor> logger) : IReceptor<RequestIntegrityManifest> {

  /// <inheritdoc />
  public async ValueTask HandleAsync(RequestIntegrityManifest message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
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
    IReadOnlyList<StreamDigest> digests;
    var recomputed = message.UseRecompute;
    if (!message.UseRecompute) {
      digests = message.Level == ManifestLevel.Types
        ? await coordinator.GetTypeDigestsAsync(null, message.EventTypes, cancellationToken).ConfigureAwait(false)
        : await coordinator.GetStreamDigestsAsync(null, message.EventTypes, cancellationToken).ConfigureAwait(false);
      // Empty table read: either genuinely nothing emitted OR the provider has no digest table
      // (the DIM default). The recompute distinguishes them — cheap when truly empty.
      recomputed = digests.Count == 0;
    } else {
      digests = [];
    }
    if (recomputed) {
      var computed = await coordinator
        .ComputeStreamDigestsAsync(null, message.EventTypes, settle, cancellationToken)
        .ConfigureAwait(false);
      digests = message.Level == ManifestLevel.Types ? IntegrityDigestMath.RollUpToTypes(computed) : computed;
    }
    if (digests.Count == 0) {
      return;   // nothing this origin emitted for those types — silence, not an empty manifest.
    }

    var originServiceId = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var originName = instanceProvider?.ServiceName ?? string.Empty;
    // All chunks of one manifest share the manifest stream (the origin id) as their session —
    // session-enabled subscriptions dead-letter sessionless deliveries, and a shared session
    // keeps chunk order.
    var destination = Whizbang.Core.Transports.ControlPlaneDestination.For(message.Topic, originServiceId);
    var chunks = 0;

    for (var offset = 0; offset < digests.Count; offset += options.MaxDigestsPerManifest) {
      var chunk = digests.Skip(offset).Take(options.MaxDigestsPerManifest).ToList();
      var envelope = new MessageEnvelope<IntegrityManifest> {
        MessageId = new MessageId(TrackedGuid.NewMedo()),
        Payload = new IntegrityManifest {
          ManifestStreamId = originServiceId,
          OriginServiceId = originServiceId,
          OriginServiceName = originName,
          Digests = chunk,
          Level = message.Level,
          Recomputed = recomputed,
        },
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTimeOffset.UtcNow,
            ServiceInstance = instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown
          }
        ],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
        Target = message.RequesterService,
      };
      var serialized = serializer.SerializeEnvelope(envelope);
      await transport.PublishAsync(serialized.JsonEnvelope, destination, serialized.EnvelopeType,
        cancellationToken: cancellationToken).ConfigureAwait(false);
      chunks++;
    }
    services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>()?.ManifestChunksSent.Add(chunks,
      new KeyValuePair<string, object?>("level", message.Level.ToString()));
    LogManifestSent(logger, digests.Count, chunks, message.RequesterService);
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

  /// <inheritdoc />
  public async ValueTask HandleAsync(IntegrityManifest message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    if (message.Digests.Count == 0) {
      return;
    }
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

    var types = message.Digests.Select(d => d.EventType).Distinct(StringComparer.Ordinal).ToList();
    var settle = TimeSpan.FromMinutes(options.AuditSettleWindowMinutes);

    if (message.Level == ManifestLevel.Types) {
      await _handleTypeLevelAsync(services, options, message, types, settle, cancellationToken).ConfigureAwait(false);
      return;
    }

    // Stream level. A sweep manifest (Recomputed) compares recompute-to-recompute — the pre-A1c
    // semantics, covering busy buckets; a table manifest compares table-to-table with settle-skip.
    var local = message.Recomputed
      ? await coordinator.ComputeStreamDigestsAsync(message.OriginServiceId, types, settle, cancellationToken).ConfigureAwait(false)
      : await _tableDigestsWithFallbackAsync(coordinator, message.OriginServiceId, types, settle, streamLevel: true, cancellationToken).ConfigureAwait(false);
    var localByBucket = local.ToDictionary(d => (d.TenantScope, d.EventType, d.StreamId));

    var repairBudget = options.MaxAutoRepairRequestsPerAudit;
    foreach (var origin in message.Digests) {
      localByBucket.TryGetValue((origin.TenantScope, origin.EventType, origin.StreamId), out var mine);
      if (mine is not null && mine.DigestLo == origin.DigestLo && mine.DigestHi == origin.DigestHi) {
        continue;   // identical fold — the bucket is provably complete.
      }
      if (!message.Recomputed && IntegrityDigestMath.IsInsideSettle(origin.UpdatedAt, mine?.UpdatedAt, settle)) {
        continue;   // the bucket changed inside the settle window — in-flight, not divergence.
      }

      var autoRepair = options.RepairMode == IntegrityRepairMode.AutoRepairCapped && repairBudget > 0;
      var metrics = services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
      metrics?.DivergencesDetected.Add(1,
        new KeyValuePair<string, object?>("origin", message.OriginServiceName),
        new KeyValuePair<string, object?>("event_type", origin.EventType));
      if (autoRepair) {
        repairBudget--;
        metrics?.RepairsRequested.Add(1,
          new KeyValuePair<string, object?>("source", "audit"),
          new KeyValuePair<string, object?>("origin", message.OriginServiceName));
        await _sendRepairRequestAsync(services, options, message, origin, cancellationToken).ConfigureAwait(false);
      }
      await dispatcher.PublishAsync(new IntegrityDivergenceDetected {
        ReportStreamId = TrackedGuid.NewMedo().Value,
        OriginServiceId = message.OriginServiceId,
        OriginServiceName = message.OriginServiceName,
        TenantScope = origin.TenantScope,
        EventType = origin.EventType,
        AuditedStreamId = origin.StreamId,
        OriginCount = origin.EventCount,
        LocalCount = mine?.EventCount ?? 0,
        AutoRepairRequested = autoRepair,
      }).ConfigureAwait(false);
      LogDivergence(logger, origin.EventType, origin.TenantScope, origin.StreamId,
        message.OriginServiceName, origin.EventCount, mine?.EventCount ?? 0, autoRepair);
    }
  }

  /// <summary>
  /// A1c: the type-level half of the hierarchical exchange. Compares the origin's per-(tenant,
  /// type) roll-ups against this consumer's; matching roll-ups prove EVERY stream bucket of the
  /// type complete — one comparison instead of thousands. Mismatched types escalate (capped) to a
  /// DIRECTED stream-level manifest request; reports only ever come from the stream-level compare.
  /// </summary>
  private async Task _handleTypeLevelAsync(
      IServiceProvider services, StreamIntegrityOptions options, IntegrityManifest message,
      List<string> types, TimeSpan settle, CancellationToken cancellationToken) {
    var coordinator = services.GetRequiredService<IWorkCoordinator>();
    var local = message.Recomputed
      ? IntegrityDigestMath.RollUpToTypes(
          await coordinator.ComputeStreamDigestsAsync(message.OriginServiceId, types, settle, cancellationToken).ConfigureAwait(false))
      : await _tableDigestsWithFallbackAsync(coordinator, message.OriginServiceId, types, settle, streamLevel: false, cancellationToken).ConfigureAwait(false);
    var localByBucket = local.ToDictionary(d => (d.TenantScope, d.EventType));

    var mismatched = new List<string>();
    foreach (var origin in message.Digests) {
      localByBucket.TryGetValue((origin.TenantScope, origin.EventType), out var mine);
      if (mine is not null && mine.DigestLo == origin.DigestLo && mine.DigestHi == origin.DigestHi) {
        continue;
      }
      if (!message.Recomputed && IntegrityDigestMath.IsInsideSettle(origin.UpdatedAt, mine?.UpdatedAt, settle)) {
        continue;
      }
      if (!mismatched.Contains(origin.EventType)) {
        mismatched.Add(origin.EventType);
      }
    }
    if (mismatched.Count == 0) {
      return;   // every type roll-up matches — the whole subscribed surface is provably complete.
    }

    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = options.RepairTopic
      ?? services.GetService<Whizbang.Core.Workers.TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
    if (transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return;   // no drill-down infrastructure — the mismatch re-audits next cycle.
    }

    var drillDown = mismatched.Take(Math.Max(0, options.MaxDrillDownTypesPerAudit)).ToList();
    if (drillDown.Count == 0) {
      return;
    }
    var envelope = new MessageEnvelope<RequestIntegrityManifest> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new RequestIntegrityManifest {
        RequesterService = requester,
        Topic = topic,
        EventTypes = drillDown,
        Level = ManifestLevel.Streams,
        UseRecompute = message.Recomputed,
      },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = message.OriginServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.ControlPlaneDestination.For(topic, envelope.MessageId.Value), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
    services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>()?.DrillDownsRequested.Add(1,
      new KeyValuePair<string, object?>("origin", message.OriginServiceName));
    LogDrillDown(logger, drillDown.Count, mismatched.Count, message.OriginServiceName);
  }

  /// <summary>Table reads, falling back to the recompute when the table has no rows — either
  /// nothing was received (recompute is equally empty, cheap) or the provider lacks the digest
  /// table (the DIM default returns empty; the recompute is the honest source).</summary>
  private static async Task<IReadOnlyList<StreamDigest>> _tableDigestsWithFallbackAsync(
      IWorkCoordinator coordinator, Guid originServiceId, List<string> types, TimeSpan settle,
      bool streamLevel, CancellationToken cancellationToken) {
    var table = streamLevel
      ? await coordinator.GetStreamDigestsAsync(originServiceId, types, cancellationToken).ConfigureAwait(false)
      : await coordinator.GetTypeDigestsAsync(originServiceId, types, cancellationToken).ConfigureAwait(false);
    if (table.Count > 0) {
      return table;
    }
    var computed = await coordinator
      .ComputeStreamDigestsAsync(originServiceId, types, settle, cancellationToken)
      .ConfigureAwait(false);
    return streamLevel ? computed : IntegrityDigestMath.RollUpToTypes(computed);
  }

  private static async Task _sendRepairRequestAsync(
      IServiceProvider services, StreamIntegrityOptions options,
      IntegrityManifest manifest, StreamDigest bucket, CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = options.RepairTopic
      ?? services.GetService<Whizbang.Core.Workers.TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
    if (transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return;   // report already published; the repair rides the next cycle when infra exists.
    }

    var envelope = new MessageEnvelope<RequestRedeliveryCommand> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new RequestRedeliveryCommand {
        TenantScope = bucket.TenantScope,
        EventTypes = [bucket.EventType],
        StreamIds = [bucket.StreamId],
        RequesterService = requester,
        Topic = topic,
      },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = manifest.OriginServiceName,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      Whizbang.Core.Transports.ControlPlaneDestination.For(topic, bucket.StreamId), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(EventId = 54, Level = LogLevel.Warning,
    Message = "AUDIT divergence: {EventType} (tenant {TenantScope}) stream {AuditedStreamId} vs origin " +
              "'{OriginServiceName}' — origin {OriginCount}, local {LocalCount} (autoRepair={AutoRepairRequested})")]
  static partial void LogDivergence(ILogger logger, string eventType, string? tenantScope, Guid auditedStreamId,
    string originServiceName, int originCount, int localCount, bool autoRepairRequested);

  [LoggerMessage(EventId = 55, Level = LogLevel.Warning,
    Message = "AUDIT type-level mismatch vs origin '{OriginServiceName}': drilling down to stream level for " +
              "{DrillDownCount} of {MismatchedCount} mismatched type(s)")]
  static partial void LogDrillDown(ILogger logger, int drillDownCount, int mismatchedCount, string originServiceName);
}

/// <summary>
/// Stream-integrity A1c: shared digest arithmetic for the hierarchical exchange.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
internal static class IntegrityDigestMath {
  /// <summary>Rolls stream-level digests up to per-(tenant, type) rows — XOR the lanes, sum the
  /// counts. Valid because stream buckets partition the type's events. Recomputed inputs carry no
  /// update times, so the roll-ups don't either.</summary>
  internal static IReadOnlyList<StreamDigest> RollUpToTypes(IReadOnlyList<StreamDigest> streamDigests) =>
    streamDigests
      .GroupBy(d => (d.TenantScope, d.EventType))
      .Select(g => new StreamDigest {
        TenantScope = g.Key.TenantScope,
        EventType = g.Key.EventType,
        StreamId = Guid.Empty,
        DigestLo = g.Aggregate(0L, (acc, d) => acc ^ d.DigestLo),
        DigestHi = g.Aggregate(0L, (acc, d) => acc ^ d.DigestHi),
        EventCount = g.Sum(d => d.EventCount),
      })
      .OrderBy(d => d.TenantScope, StringComparer.Ordinal).ThenBy(d => d.EventType, StringComparer.Ordinal)
      .ToList();

  /// <summary>True when either side's bucket changed inside the settle window — the table-driven
  /// equivalent of the recompute's created-at settle filter: an in-flight delivery must never
  /// read as divergence. Null update times (recomputed rows) never skip.</summary>
  internal static bool IsInsideSettle(DateTimeOffset? originUpdatedAt, DateTimeOffset? localUpdatedAt, TimeSpan settle) {
    var floor = DateTimeOffset.UtcNow - settle;
    return originUpdatedAt > floor || localUpdatedAt > floor;
  }
}

/// <summary>
/// Registers the Phase A manifest receptors with <see cref="IReceptorRegistry"/> at startup — the
/// same runtime-registration rationale as every framework receptor in this assembly.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs</tests>
internal sealed class IntegrityManifestReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrityManifestRequestReceptor> requestLogger,
    ILogger<IntegrityManifestReceptor> manifestLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    if (registry is null) {
      return Task.CompletedTask;
    }
    var requestReceptor = new IntegrityManifestRequestReceptor(scopeFactory, requestLogger);
    registry.Register<RequestIntegrityManifest>(requestReceptor, LifecycleStage.LocalImmediateInline);
    registry.Register<RequestIntegrityManifest>(requestReceptor, LifecycleStage.PreOutboxInline);
    registry.Register<RequestIntegrityManifest>(requestReceptor, LifecycleStage.PostInboxInline);
    var manifestReceptor = new IntegrityManifestReceptor(scopeFactory, manifestLogger);
    registry.Register<IntegrityManifest>(manifestReceptor, LifecycleStage.LocalImmediateInline);
    registry.Register<IntegrityManifest>(manifestReceptor, LifecycleStage.PreOutboxInline);
    registry.Register<IntegrityManifest>(manifestReceptor, LifecycleStage.PostInboxInline);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
