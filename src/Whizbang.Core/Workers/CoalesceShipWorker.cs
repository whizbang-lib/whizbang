using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// The generic tag-bound coalesce shipper: a true sliding window per coalesce group. Each tick
/// it reads per-group pending stats and folds a group when it has gone QUIET (newest pending
/// single older than the binding's SlideSeconds — a burst's entire tail ships at burst-end) or
/// OVERDUE (oldest older than MaxDelaySeconds — the freshness cap forces an oldest-first ship
/// under continuous arrivals). Folds fetch in MaxBatchCount chunks, build the binding's
/// composite (default: raw-carry <see cref="CoalescedEventsComposite"/>; the built-in audit
/// binding supplies <c>AuditEventsComposite</c>), and complete fold + composite insert in one
/// transaction through <see cref="IWorkCoordinator.CompleteCoalesceFoldAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recovery / backstop.</b> On startup the worker releases every enabled group's matured
/// rows once (rows whose floor elapsed while no shipper ran degrade to individual shipping
/// immediately), and each tick — AFTER the fold pass, because matured rows prefer folding —
/// releases whatever the fold could not claim, including rows of groups that are no longer
/// bound. Releases are counted via a Warning log; OTel meters (pending-per-group gauge,
/// folds/ships/deadline-degrades counters, coalesce latency histogram) are a later increment —
/// see the tag-bound-coalescing proposal, increment 6.
/// </para>
/// <para>
/// <b>Gating.</b> Registered unconditionally (the worker-pipeline idiom — coalesce bindings
/// finalize after AddWhizbang, e.g. when <c>EnableAudit()</c> registers the built-in audit
/// binding later in composition), the worker parks at ExecuteAsync when no enabled binding
/// exists: the coordinator is never touched, no tick ever runs.
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-tags#coalescing</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerTests.cs:RunOnce_GroupQuiet_FoldsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerTests.cs:RunOnce_ContinuousArrivalsPastMaxDelay_FoldsAnywayAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerTests.cs:RunOnce_PendingLargerThanMaxBatch_FoldsInChunksAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerTests.cs:ExecuteAsync_NoEnabledBindings_ParksWithoutTouchingTheCoordinatorAsync</tests>
public sealed partial class CoalesceShipWorker(
  IServiceScopeFactory scopeFactory,
  ISchemaReadyGate schemaReadyGate,
  CoalesceGroupResolver? coalesceResolver = null,
  ILogger<CoalesceShipWorker>? logger = null,
  TimeProvider? timeProvider = null,
  IServiceInstanceProvider? instanceProvider = null) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly CoalesceGroupResolver? _coalesceResolver = coalesceResolver;
  private readonly ILogger<CoalesceShipWorker>? _logger = logger;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
  private readonly IServiceInstanceProvider? _instanceProvider = instanceProvider;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (_coalesceResolver is null || !_coalesceResolver.HasEnabledBindings) {
      // No enabled coalesce binding — the feature is unused in this host. Park (keep
      // ExecuteTask alive, the MaintenanceWorker killswitch idiom) rather than exit, so a
      // health probe never mistakes "unused" for "crashed".
      if (_logger is not null) {
        LogParkedNoBindings(_logger);
      }
      try {
        await Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) { }
      return;
    }

    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return;
    }

    if (_logger is not null) {
      var groups = string.Join(", ", _enabledGroups());
      LogStarted(_logger, groups);
    }

    // Startup recovery: rows whose floor matured while no shipper ran degrade to individual
    // shipping NOW rather than waiting out a tick.
    try {
      await RunStartupRecoveryAsync(stoppingToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return;
    } catch (Exception ex) {
      if (_logger is not null) {
        LogRecoveryFailed(_logger, ex);
      }
    }

    // First tick runs immediately after recovery: a restart with an already-quiet backlog
    // folds NOW instead of one tick later. Subsequent ticks pace on the interval.
    var tick = _computeTickInterval();
    while (!stoppingToken.IsCancellationRequested) {
      try {
        await RunOnceAsync(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        if (_logger is not null) {
          LogTickFailed(_logger, ex);
        }
      }

      try {
        await Task.Delay(tick, _timeProvider, stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      }
    }
  }

  /// <summary>
  /// Startup recovery: one release pass over every enabled group. Internal seam for tests.
  /// </summary>
  internal async Task RunStartupRecoveryAsync(CancellationToken cancellationToken) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    foreach (var group in _enabledGroups()) {
      var released = await coordinator.ReleaseMaturedCoalesceAsync(group, cancellationToken).ConfigureAwait(false);
      if (released > 0 && _logger is not null) {
        LogReleasedMatured(_logger, released, group);
      }
    }
  }

  /// <summary>
  /// One tick: per-group stats → fold the due groups (quiet or overdue) in MaxBatchCount
  /// chunks → release-matured backstop pass. Internal seam so tests drive decisions without
  /// wall-clock waits.
  /// </summary>
  internal async Task RunOnceAsync(CancellationToken cancellationToken) {
    using var scope = _scopeFactory.CreateScope();
    var sp = scope.ServiceProvider;
    var coordinator = sp.GetRequiredService<IWorkCoordinator>();
    var serializer = sp.GetRequiredService<IEnvelopeSerializer>();
    var partitionCount = sp.GetService<WorkCoordinatorOptions>()?.PartitionCount ?? 10_000;

    var stats = await coordinator.GetPendingCoalesceGroupStatsAsync(cancellationToken).ConfigureAwait(false);
    var now = _timeProvider.GetUtcNow();

    // Fold pass FIRST: matured rows prefer folding — batching them is the whole feature.
    foreach (var groupStats in stats) {
      var binding = _coalesceResolver!.GetBinding(groupStats.Group);
      if (binding is null || groupStats.PendingCount <= 0) {
        continue;  // unbound/disabled strays surface via the release backstop below
      }

      var quiet = now - groupStats.NewestCreatedAt >= TimeSpan.FromSeconds(binding.SlideSeconds);
      var overdue = now - groupStats.OldestCreatedAt >= TimeSpan.FromSeconds(binding.MaxDelaySeconds);
      if (quiet || overdue) {
        await _foldGroupAsync(coordinator, serializer, groupStats.Group, binding, partitionCount, cancellationToken).ConfigureAwait(false);
      }
    }

    // Release backstop AFTER the fold: whatever a fold could not claim (or belongs to a group
    // nobody binds anymore) and has blown its floor ships individually — degraded, never lost.
    foreach (var groupStats in stats) {
      var released = await coordinator.ReleaseMaturedCoalesceAsync(groupStats.Group, cancellationToken).ConfigureAwait(false);
      if (released > 0 && _logger is not null) {
        LogReleasedMatured(_logger, released, groupStats.Group);
      }
    }
  }

  private async Task _foldGroupAsync(
      IWorkCoordinator coordinator,
      IEnvelopeSerializer serializer,
      string group,
      CoalescePolicyOptions binding,
      int partitionCount,
      CancellationToken cancellationToken) {
    while (!cancellationToken.IsCancellationRequested) {
      var singles = await coordinator.FetchPendingCoalesceAsync(group, binding.MaxBatchCount, cancellationToken).ConfigureAwait(false);
      if (singles.Count == 0) {
        break;
      }

      // A composite has ONE destination — split a mixed fetch per destination.
      foreach (var destinationBatch in singles.GroupBy(m => m.Destination, StringComparer.Ordinal)) {
        var batchSingles = destinationBatch.ToList();
        var batch = new CoalesceFoldBatch {
          Group = group,
          Singles = batchSingles,
          Atomicity = binding.Atomicity
        };
        var composite = (binding.CompositeFactory ?? BuildDefaultComposite)(batch);
        var compositeMessage = _buildCompositeOutboxMessage(serializer, composite, destinationBatch.Key);

        await coordinator.CompleteCoalesceFoldAsync(
          [.. batchSingles.Select(m => m.MessageId)],
          [compositeMessage],
          partitionCount,
          cancellationToken).ConfigureAwait(false);

        if (_logger is not null) {
          LogFolded(_logger, batchSingles.Count, group, compositeMessage.MessageId);
        }
      }

      if (singles.Count < binding.MaxBatchCount) {
        break;  // the group has drained below one chunk
      }
    }
  }

  /// <summary>
  /// The default composite factory: the generic raw-carry <see cref="CoalescedEventsComposite"/>
  /// carrying each single's stored payload, wire type name, and ORIGINAL message id.
  /// </summary>
  /// <param name="batch">The fold batch.</param>
  /// <returns>The built composite.</returns>
  public static CompositeEventBase BuildDefaultComposite(CoalesceFoldBatch batch) {
    ArgumentNullException.ThrowIfNull(batch);
    return new CoalescedEventsComposite {
      StreamId = TrackedGuid.NewMedo(),
      Atomicity = batch.Atomicity,
      InnerPayloads = [.. batch.Singles.Select(m => m.Envelope.Payload)],
      InnerTypeNames = [.. batch.Singles.Select(m => m.MessageType)],
      InnerEventIds = [.. batch.Singles.Select(m => m.MessageId)]
    };
  }

  private OutboxMessage _buildCompositeOutboxMessage(
      IEnvelopeSerializer serializer,
      CompositeEventBase composite,
      string? destination) {
    var envelope = new MessageEnvelope<CompositeEventBase> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = composite,
      Hops = [
        new MessageHop {
          ServiceInstance = _instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = _timeProvider.GetUtcNow()
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };

    // The outbox's composite seam: typed → MessageEnvelope<JsonElement> + payload-derived wire
    // type names (the runtime composite type, not CompositeEventBase — the serializer resolves
    // the payload's concrete JsonTypeInfo, so binding-supplied composites ride source-gen too).
    var serialized = serializer.SerializeEnvelope(envelope);

    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = destination,
      Envelope = serialized.JsonEnvelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? []
      },
      EnvelopeType = serialized.EnvelopeType,
      StreamId = composite.StreamId,
      // Transport-only: event singles were already event-stored at their own mint; the
      // composite must not re-enter the event store. CoalesceGroup stays null BY DESIGN —
      // the composite ships immediately, never back into the pool it just drained.
      IsEvent = false,
      MessageType = serialized.MessageType
    };
  }

  private IReadOnlyList<string> _enabledGroups() =>
    _coalesceResolver is null
      ? []
      : [.. _coalesceResolver.EnabledGroups];

  private TimeSpan _computeTickInterval() {
    // min(SlideSeconds)/3, floor 1s: three chances to observe a quiet window before it could
    // have elapsed, without hot-polling.
    var minSlide = int.MaxValue;
    foreach (var group in _enabledGroups()) {
      var binding = _coalesceResolver!.GetBinding(group);
      if (binding is not null && binding.SlideSeconds < minSlide) {
        minSlide = binding.SlideSeconds;
      }
    }
    var seconds = minSlide == int.MaxValue ? 5 : Math.Max(1, minSlide / 3);
    return TimeSpan.FromSeconds(seconds);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "CoalesceShipWorker started; enabled groups: {Groups}")]
  static partial void LogStarted(ILogger logger, string groups);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
    Message = "CoalesceShipWorker parked: no enabled coalesce bindings in this host")]
  static partial void LogParkedNoBindings(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "Released {Count} matured coalesce-pending singles for group '{Group}' to individual shipping (deadline degrade — the fold did not get to them in time)")]
  static partial void LogReleasedMatured(ILogger logger, int count, string group);

  [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
    Message = "Folded {Count} pending singles of group '{Group}' into composite {CompositeMessageId}")]
  static partial void LogFolded(ILogger logger, int count, string group, Guid compositeMessageId);

  [LoggerMessage(EventId = 5, Level = LogLevel.Error,
    Message = "CoalesceShipWorker tick failed; will retry next tick")]
  static partial void LogTickFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 6, Level = LogLevel.Error,
    Message = "CoalesceShipWorker startup recovery release failed; per-tick backstop will retry")]
  static partial void LogRecoveryFailed(ILogger logger, Exception ex);
}
