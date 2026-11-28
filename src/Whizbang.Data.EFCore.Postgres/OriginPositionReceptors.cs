// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The ORIGIN's side of filling missing origin positions (#2544): answers a received <see cref="RequestOriginPositions"/>
/// with this service's commit sequence for each asked event it wrote (from its store, or its archive table when the
/// store no longer holds it), the events it holds but has not stamped yet, and the events it cannot place. Published to
/// the asker's address, targeted at the asker, like a manifest answer.
/// </summary>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillE2ETests.cs</tests>
public sealed partial class RequestOriginPositionsReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<RequestOriginPositionsReceptor> logger) : IReceptor<RequestOriginPositions> {

  /// <inheritdoc />
  public async ValueTask HandleAsync(RequestOriginPositions message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await using var scope = scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var database = services.GetService<PostgresArchiveConnection>();
    var coordinator = services.GetService<IWorkCoordinator>();
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    if (database is null || coordinator is null || transport is null || serializer is null) {
      LogMissingInfrastructure(logger, message.RequesterService);
      return;
    }
    var options = services.GetService<IOptions<StreamIntegrityOptions>>()?.Value ?? new StreamIntegrityOptions();
    var lookups = await new OriginPositionStore(database).PositionsOfAsync(message.EventIds, cancellationToken).ConfigureAwait(false);
    var originServiceId = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var answer = new OriginPositionsAnswer {
      OriginServiceId = originServiceId,
      OriginServiceName = instanceProvider?.ServiceName,
      RequestId = message.RequestId,
      // The lookup gives a positioned or archived event its sequence, always.
      Positions = [.. lookups.Where(l => l.State is "positioned" or "archived")
        .Select(l => new OriginPosition(l.EventId, l.CommitSequence!.Value) {
          // #2588: the bucket it is counted in, for an asker that excluded it.
          TenantScope = l.TenantScope, EventType = l.EventType, StreamId = l.StreamId,
        })],
      NotYetStamped = [.. lookups.Where(l => l.State == "unstamped").Select(l => l.EventId)],
      Unknown = [.. lookups.Where(l => l.State == "unknown").Select(l => l.EventId)],
    };
    var envelope = new MessageEnvelope<OriginPositionsAnswer> {
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
      Payload = answer,
      Hops = [ControlPlaneHop.Create(typeof(OriginPositionsAnswer), instanceProvider, DateTimeOffset.UtcNow)],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = message.RequesterService,
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      IntegrityControlDestination.For(message.Topic, originServiceId, typeof(OriginPositionsAnswer), options),
      serialized.EnvelopeType, cancellationToken: cancellationToken).ConfigureAwait(false);
    LogAnswered(logger, message.RequesterService, answer.Positions.Count, answer.NotYetStamped.Count, answer.Unknown.Count);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Answered '{RequesterService}' with origin positions: {Positioned} positioned, {NotYet} not stamped yet, {Unknown} unknown (#2544)")]
  static partial void LogAnswered(ILogger logger, string requesterService, int positioned, int notYet, int unknown);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "RequestOriginPositions from '{RequesterService}' received, but the database, work coordinator, transport or serializer is not registered; ignored")]
  static partial void LogMissingInfrastructure(ILogger logger, string requesterService);
}

/// <summary>
/// The ASKER's side of filling missing origin positions (#2544): applies a received <see cref="OriginPositionsAnswer"/>.
/// Only events this service holds from the answering origin without a position are filled, never an overwrite; every
/// sealed epoch that now holds a filled event is refolded; events the origin cannot place are recorded and never asked
/// again; a stream whose replay order the fill changes is rewound from its start (<see cref="OriginPositionStore.FillAsync"/>).
/// One Information line per answer says what it did.
/// </summary>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillE2ETests.cs</tests>
public sealed partial class OriginPositionsAnswerReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<OriginPositionsAnswerReceptor> logger) : IReceptor<OriginPositionsAnswer> {

  /// <inheritdoc />
  public async ValueTask HandleAsync(OriginPositionsAnswer message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await using var scope = scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var database = services.GetService<PostgresArchiveConnection>();
    if (database is null) {
      LogMissingInfrastructure(logger, message.OriginServiceId);
      return;
    }
    var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
    // #2588: the answer settles the pending exclusions it names first. An excluded event is resolved by decision, so its
    // position is not filled (a fill can reorder a stream and rewind its perspectives) and its unknown answer not kept.
    var excluded = await new IntegrityExclusionStore(database).SettleAsync(message, now, cancellationToken).ConfigureAwait(false);
    if (excluded.Count > 0) {
      LogExclusionsSettled(logger, excluded.Count, message.OriginServiceName, message.OriginServiceId);
      message = message with {
        Positions = [.. message.Positions.Where(p => !excluded.Contains(p.EventId))],
        NotYetStamped = [.. message.NotYetStamped.Where(id => !excluded.Contains(id))],
        Unknown = [.. message.Unknown.Where(id => !excluded.Contains(id))],
      };
    }
    var result = await new OriginPositionStore(database).FillAsync(message, now, cancellationToken).ConfigureAwait(false);
    var metrics = services.GetService<StreamIntegrityMetrics>();
    if (metrics is not null) {
      var origin = new KeyValuePair<string, object?>("origin", message.OriginServiceName ?? message.OriginServiceId.ToString());
      metrics.OriginPositionsFilled.Add(result.Filled, origin);
      metrics.OriginPositionsUnfilled.Add(message.NotYetStamped.Count, origin, new KeyValuePair<string, object?>("answer", "not_yet_stamped"));
      metrics.OriginPositionsUnfilled.Add(message.Unknown.Count, origin, new KeyValuePair<string, object?>("answer", "unknown"));
      metrics.OriginPositionRewinds.Add(result.PerspectivesRewound);
      metrics.OriginPositionRewindsSkipped.Add(result.RewindsSkipped);
    }
    if (result.FirstSkippedRewind is { } first) {
      LogRewindsSkipped(logger, result.RewindsSkipped, first.PerspectiveName, first.StreamId, first.TriggerEventId);
    }
    LogFilled(logger, result.Filled, message.Positions.Count, message.OriginServiceName, message.OriginServiceId,
      message.NotYetStamped.Count, result.UnknownRecorded, result.StreamsReordered, result.PerspectivesRewound);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "A generated structured log message: each value is its own searchable property.")]
  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Filled {Filled} of {Answered} origin position(s) answered by '{OriginServiceName}' ({OriginServiceId}); " +
              "{NotYet} not stamped there yet (asked again next lap), {Unknown} unknown there (not asked again); " +
              "{Reordered} stream(s) reordered, {Rewound} perspective cursor(s) rewound (#2544)")]
  static partial void LogFilled(ILogger logger, int filled, int answered, string? originServiceName, Guid originServiceId,
    int notYet, int unknown, int reordered, int rewound);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "Skipped {Count} rewind(s) the filled positions asked for: no event at or after the changed version that the " +
              "perspective reads still has a body (reaped past its retention), so nothing can be replayed and the cursor " +
              "stays where it is (first: perspective {PerspectiveName}, stream {StreamId}, from event {TriggerEventId}) (#2591)")]
  static partial void LogRewindsSkipped(ILogger logger, int count, string perspectiveName, Guid streamId, Guid triggerEventId);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information,
    Message = "The answer from '{OriginServiceName}' ({OriginServiceId}) named {Count} event(s) excluded from integrity: their " +
              "marks are settled, and their positions are not filled (#2588)")]
  static partial void LogExclusionsSettled(ILogger logger, int count, string? originServiceName, Guid originServiceId);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Origin positions answered by {OriginServiceId} received, but no database connection is registered; ignored")]
  static partial void LogMissingInfrastructure(ILogger logger, Guid originServiceId);
}

/// <summary>
/// Registers the origin-position receptors (#2544) with <see cref="IReceptorRegistry"/> at startup, deferred at every
/// stage (<see cref="IntegrityDeliveryRegistration"/>): answering and filling run on the maintainer's integrity delivery
/// drain, like every integrity exchange.
/// </summary>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityReceptorRegistrationEnforcementTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6672:Generic logger injection should match enclosing type", Justification = "The registrar never logs. It receives the loggers for the receptors it constructs and hands them straight over, so each category names the type that actually writes the entries.")]
internal sealed class OriginPositionReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<RequestOriginPositionsReceptor> requestLogger,
    ILogger<OriginPositionsAnswerReceptor> answerLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    if (registry is null or INullDefault) {
      return Task.CompletedTask;
    }
    registry.RegisterIntegrityReceptor(new RequestOriginPositionsReceptor(scopeFactory, requestLogger), services, scopeFactory);
    registry.RegisterIntegrityReceptor(new OriginPositionsAnswerReceptor(scopeFactory, answerLogger), services, scopeFactory);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
