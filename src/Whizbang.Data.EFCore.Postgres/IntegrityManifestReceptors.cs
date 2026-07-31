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
/// <see cref="RequestIntegrityManifest"/> with this service's identity digests
/// (<see cref="IWorkCoordinator.ComputeStreamDigestsAsync"/>, own-emissions flavor), published
/// wire-only as <see cref="IntegrityManifest"/> chunks TARGETED back at the requester. An origin
/// with no matching history stays silent — comparison is per-bucket, so absence means nothing to
/// audit, and a lost chunk's buckets simply re-audit next cycle.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
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
    var digests = await coordinator
      .ComputeStreamDigestsAsync(null, message.EventTypes, settle, cancellationToken)
      .ConfigureAwait(false);
    if (digests.Count == 0) {
      return;   // nothing this origin emitted for those types — silence, not an empty manifest.
    }

    var originServiceId = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var originName = instanceProvider?.ServiceName ?? string.Empty;
    var destination = new TransportDestination(message.Topic);
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
/// <docs>proposals/stream-integrity</docs>
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
    var local = await coordinator
      .ComputeStreamDigestsAsync(message.OriginServiceId, types, settle, cancellationToken)
      .ConfigureAwait(false);
    var localByBucket = local.ToDictionary(d => (d.TenantScope, d.EventType, d.StreamId));

    var repairBudget = options.MaxAutoRepairRequestsPerAudit;
    foreach (var origin in message.Digests) {
      localByBucket.TryGetValue((origin.TenantScope, origin.EventType, origin.StreamId), out var mine);
      if (mine is not null && mine.DigestLo == origin.DigestLo && mine.DigestHi == origin.DigestHi) {
        continue;   // identical fold — the bucket is provably complete.
      }

      var autoRepair = options.RepairMode == IntegrityRepairMode.AutoRepairCapped && repairBudget > 0;
      if (autoRepair) {
        repairBudget--;
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
    await transport.PublishAsync(serialized.JsonEnvelope, new TransportDestination(topic), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(EventId = 54, Level = LogLevel.Warning,
    Message = "AUDIT divergence: {EventType} (tenant {TenantScope}) stream {AuditedStreamId} vs origin " +
              "'{OriginServiceName}' — origin {OriginCount}, local {LocalCount} (autoRepair={AutoRepairRequested})")]
  static partial void LogDivergence(ILogger logger, string eventType, string? tenantScope, Guid auditedStreamId,
    string originServiceName, int originCount, int localCount, bool autoRepairRequested);
}

/// <summary>
/// Registers the Phase A manifest receptors with <see cref="IReceptorRegistry"/> at startup — the
/// same runtime-registration rationale as every framework receptor in this assembly.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
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
