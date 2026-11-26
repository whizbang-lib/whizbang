// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one origin-position fill cycle did.</summary>
/// <param name="Batches">The batches the walk took.</param>
/// <param name="Requested">Events named in the requests sent.</param>
/// <param name="Status"><c>caught-up</c> (no origin is due), <c>budget</c> (the cycle's batch budget ran out first), or
/// <c>skipped (...)</c>.</param>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Step_AsksEachOriginInBoundedBatches_CountsAndLogsEachAsync</tests>
public readonly record struct OriginPositionFillCycle(int Batches, long Requested, string Status);

/// <summary>
/// The maintenance step that asks origins for the commit sequences of the events this service received from them without
/// one (#2544). Such an event sits in no audit window, so the windows it belongs to stay undecided and the seal for the
/// origin cannot advance; only the origin knows the position.
/// </summary>
/// <remarks>
/// <para>
/// Each cycle publishes the per-origin count still waiting (<c>whizbang.stream_integrity.origin_positions_unfilled</c>),
/// then takes up to <see cref="OriginPositionFillOptions.MaxBatchesPerCycle"/> batches of
/// <see cref="OriginPositionFillOptions.BatchSize"/> held rows (<see cref="OriginPositionStore.AskAsync"/>), each one
/// origin's next rows after its durable cursor, and sends each as a <see cref="RequestOriginPositions"/> directed at that
/// origin's announced request address, the way the audit sends a manifest request. The origin answers with
/// <see cref="OriginPositionsAnswer"/>; the answer's receptor fills the positions. A batch whose origin this service does
/// not audit (no request address) is not sent: its rows are asked again on the walk's next lap.
/// </para>
/// <para>
/// It runs in the maintenance cycle, so it is idle-gated and runs only on the maintainer turn: under a bulk import the
/// cycle is deferred like the rest of maintenance, and once admitted its read is bounded by its batch budget. Off with
/// <see cref="OriginPositionFillOptions.Enabled"/> set to <see langword="false"/>.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillE2ETests.cs</tests>
public sealed partial class OriginPositionFillStep : IMaintenanceStep {
  private const string CAUGHT_UP = "caught-up";
  private const string BUDGET = "budget";

  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each batch.</param>
  public OriginPositionFillStep(ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Step_IsSkipped_WhenOffOrWithoutTheWire_AndItsArgumentsAreCheckedAsync</tests>
  public static OriginPositionFillStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new OriginPositionFillStep(services.GetService<ILogger<OriginPositionFillStep>>() ?? (ILogger)NullLogger.Instance);
  }

  /// <inheritdoc />
  public string Name => "origin-position-fill";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    _ = await FillAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services: the database through <see cref="PostgresArchiveConnection"/>,
  /// the options, the origins this service audits (<see cref="IntegrityGapTracker"/>), and the wire.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  public async Task<OriginPositionFillCycle> FillAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var integrity = services.GetService<IOptions<StreamIntegrityOptions>>()?.Value ?? new StreamIntegrityOptions();
    var options = integrity.OriginPositionFill;
    if (!options.Enabled) {
      return _skipped("skipped (Whizbang:StreamIntegrity:OriginPositionFill:Enabled=false)");
    }
    var store = new OriginPositionStore(services.GetRequiredService<PostgresArchiveConnection>());
    var metrics = services.GetService<StreamIntegrityMetrics>();
    metrics?.UpdateUnfilledOriginPositions(await store.UnfilledAsync(cancellationToken).ConfigureAwait(false));

    var tracker = services.GetService<IntegrityGapTracker>();
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = RepairTopicResolver.Resolve(integrity.RepairTopic, services);
    if (tracker is null || transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      return _skipped("skipped (no integrity wire: an origin tracker, transport, serializer, service name and reply address are all needed)");
    }
    var coordinator = services.GetService<IWorkCoordinator>();
    if (coordinator is not null) {
      await tracker.EnsureHydratedAsync(coordinator, cancellationToken).ConfigureAwait(false);
    }
    var origins = tracker.GetOrigins()
      .Where(o => !string.IsNullOrEmpty(o.RequestTopic))
      .ToDictionary(o => o.OriginServiceId, o => (Name: o.OriginServiceName, Topic: o.RequestTopic!));
    var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;

    var batches = 0;
    long requested = 0;
    while (batches < options.MaxBatchesPerCycle) {
      var ask = await store.AskAsync(options.BatchSize, options.LapPause, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
      if (ask is null) {
        return new OriginPositionFillCycle(batches, requested, CAUGHT_UP);
      }
      batches++;
      if (ask.EventIds.Count == 0) {
        continue;
      }
      if (!origins.TryGetValue(ask.OriginServiceId, out var origin)) {
        LogNotAudited(_logger, ask.EventIds.Count, ask.OriginServiceId);
        continue;
      }
      var envelope = new MessageEnvelope<RequestOriginPositions> {
        Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
        MessageId = new MessageId(TrackedGuid.New()),
        Payload = new RequestOriginPositions {
          RequesterService = requester,
          Topic = topic,
          EventIds = ask.EventIds,
          RequestId = TrackedGuid.New(),
        },
        Hops = [ControlPlaneHop.Create(typeof(RequestOriginPositions), instanceProvider, clock.GetUtcNow())],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
        Target = origin.Name,
      };
      var serialized = serializer.SerializeEnvelope(envelope);
      await transport.PublishAsync(serialized.JsonEnvelope,
        IntegrityControlDestination.For(origin.Topic, ask.OriginServiceId, typeof(RequestOriginPositions), integrity),
        serialized.EnvelopeType, cancellationToken: cancellationToken).ConfigureAwait(false);
      requested += ask.EventIds.Count;
      metrics?.OriginPositionsRequested.Add(ask.EventIds.Count, new KeyValuePair<string, object?>("origin", origin.Name));
      LogAsked(_logger, ask.EventIds.Count, origin.Name, ask.OriginServiceId, ask.LapEnded);
    }
    return new OriginPositionFillCycle(batches, requested, BUDGET);
  }

  private OriginPositionFillCycle _skipped(string status) {
    LogSkipped(_logger, status);
    return new OriginPositionFillCycle(0, 0, status);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Asked origin '{OriginServiceName}' ({OriginServiceId}) for the commit sequences of {Count} received event(s) " +
              "stored without one; lap ended: {LapEnded} (#2544)")]
  static partial void LogAsked(ILogger logger, int count, string originServiceName, Guid originServiceId, bool lapEnded);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "{Count} received event(s) from origin {OriginServiceId} are stored without its commit sequence, but this " +
              "service does not audit that origin (no checkpoint with a request address), so they are not asked for (#2544)")]
  static partial void LogNotAudited(ILogger logger, int count, Guid originServiceId);

  [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Origin position fill {Status}")]
  static partial void LogSkipped(ILogger logger, string status);
}
