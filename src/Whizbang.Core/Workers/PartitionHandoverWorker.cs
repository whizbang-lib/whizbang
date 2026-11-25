// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Something that works or holds streams, and so takes part in handing a partition over (#2557): it says which streams
/// it is working now, and saves what it holds for the streams of a partition about to move.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/PartitionHandoverWorkerTests.cs</tests>
public interface IPartitionHandoverParticipant {
  /// <summary>The streams being worked now; a partition is handed over only once none of its streams is.</summary>
  IEnumerable<Guid> StreamsInFlight { get; }

  /// <summary>
  /// Saves what is held for the streams about to move, before the old owner acknowledges: a clean handover saves what
  /// the owner would have saved anyway, so nothing is replayed (design 3.4, step 4).
  /// </summary>
  /// <param name="handsOver">Whether a stream's partition is being handed over now.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes once everything held for those streams is saved.</returns>
  Task SaveForHandoverAsync(Func<Guid, bool> handsOver, CancellationToken cancellationToken);
}

/// <summary>The drain channels as a handover participant: the streams their drains are working; nothing held to save.</summary>
/// <param name="outbox">The outbox drain channel.</param>
/// <param name="inbox">The inbox drain channel.</param>
/// <param name="perspective">The perspective drain channel.</param>
/// <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/PartitionHandoverWorkerTests.cs:DrainChannels_ReportTheStreamsTheirDrainsAreWorkingAsync</tests>
public sealed class DrainChannelsHandoverParticipant(
    IOutboxDrainChannel outbox, IInboxDrainChannel inbox, IPerspectiveDrainChannel perspective) : IPartitionHandoverParticipant {
  /// <inheritdoc />
  public IEnumerable<Guid> StreamsInFlight => outbox.InFlightStreams.Concat(inbox.InFlightStreams).Concat(perspective.InFlightStreams);

  /// <inheritdoc />
  public Task SaveForHandoverAsync(Func<Guid, bool> handsOver, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The handover worker's optional collaborators (#2557).</summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/PartitionHandoverWorkerLoopTests.cs</tests>
public sealed record PartitionHandoverWorkerSettings {
  /// <summary>The queued-writes signal, when the worker pipeline registers one; the acknowledgement waits on it.</summary>
  public IQueuedWritesLandedSignal? Landed { get; init; }

  /// <summary>
  /// The startup stages, when registered: an instance that never reached the heartbeat's start point cannot have been
  /// given a partition, so its graceful leave has nothing to hand over and asks the database nothing.
  /// </summary>
  public Whizbang.Core.Startup.IStartupStageGate? Stages { get; init; }

  /// <summary>The clock the interval is waited on; the system clock when null.</summary>
  public TimeProvider? Time { get; init; }

  /// <summary>How long a graceful leave waits for its partitions to move; the handover deadline when null.</summary>
  public TimeSpan? LeaveBudget { get; init; }
}

/// <summary>
/// The old owner's side of a handover (#2557, design 3.4). Once a second, it asks which partitions it is handing over;
/// for each one none of whose streams is still being worked, it has every participant save what it holds there, waits
/// for its queued writes to land under the old epoch, and acknowledges, which releases what it has not started and flips
/// the partition to its next owner at a raised epoch. A partition whose batch is still in flight waits for a later pass;
/// one that never finishes is completed by the assigner at its deadline. Nothing hands over unless the service owns
/// partitions by map.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/PartitionHandoverWorkerTests.cs</tests>
/// <param name="scopeFactory">Scopes for the work coordinator.</param>
/// <param name="instance">This instance.</param>
/// <param name="tenures">The tenure ledger, which says each claimed stream's partition.</param>
/// <param name="participants">
/// Everything that works or holds streams, enumerated afresh on each pass, so a participant is resolved only once a
/// handover needs it.
/// </param>
/// <param name="logger">The logger.</param>
/// <param name="settings">The optional collaborators.</param>
public sealed partial class PartitionHandoverWorker(
    IServiceScopeFactory scopeFactory,
    IServiceInstanceProvider instance,
    PartitionTenures tenures,
    IEnumerable<IPartitionHandoverParticipant> participants,
    ILogger<PartitionHandoverWorker> logger,
    PartitionHandoverWorkerSettings? settings = null) : BackgroundService, Whizbang.Core.RunControl.IReleasesHeldWork {
  /// <summary>How often the worker looks for partitions to hand over.</summary>
  public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IServiceInstanceProvider _instance = instance ?? throw new ArgumentNullException(nameof(instance));
  private readonly PartitionTenures _tenures = tenures ?? throw new ArgumentNullException(nameof(tenures));
  private readonly IEnumerable<IPartitionHandoverParticipant> _participants =
    participants ?? throw new ArgumentNullException(nameof(participants));
  private readonly IQueuedWritesLandedSignal? _landed = settings?.Landed;
  private readonly Whizbang.Core.Startup.IStartupStageGate? _stages = settings?.Stages;
  private readonly ILogger<PartitionHandoverWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly TimeProvider _time = settings?.Time ?? TimeProvider.System;
  private readonly TimeSpan _leaveBudget = settings?.LeaveBudget ?? new PartitionAssignerOptions().HandoverDeadline;

  /// <summary>
  /// Graceful leave (#2557, design 3.3): a stopping instance hands over every partition it owns before it deregisters.
  /// The run control calls this once the instance is draining (so the assigner gives it no partition) and has finished
  /// the work in hand. Each interval it hands over what the assigner has moved, until it owns nothing; it returns at
  /// once when no other live instance could take its partitions, and gives up after the handover deadline, past which
  /// the assigner completes the moves itself.
  /// </summary>
  /// <param name="cancellationToken">The host's stop budget.</param>
  /// <returns>A task that completes once nothing is owned, or the wait is over; at once before the heartbeat's start point.</returns>
  /// <docs>fundamentals/work-coordinator/partition-assignment#graceful-leave</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/PartitionHandoverWorkerLoopTests.cs</tests>
  public async Task ReleaseHeldWorkAsync(CancellationToken cancellationToken) {
    if (_stages?.HasReached(HeartbeatWorker.StartsAt) == false) {
      // Never live to the assigner, so never given a partition: a stop before then has nothing to hand over.
      return;
    }
    using var timer = new PeriodicTimer(Interval, _time);
    var started = _time.GetTimestamp();
    while (true) {
      PartitionLeaveStatus status;
      await using (var scope = _scopeFactory.CreateAsyncScope()) {
        var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
        status = await coordinator.GetLeaveStatusAsync(_instance.InstanceId, cancellationToken).ConfigureAwait(false);
      }
      if (status.Owned == 0 || status.OtherLiveCandidates == 0) {
        LogLeft(_logger, status.Owned);
        return;
      }
      _ = await HandOverAsync(cancellationToken).ConfigureAwait(false);
      if (_time.GetElapsedTime(started) >= _leaveBudget) {
        LogLeaveGaveUp(_logger, status.Owned, _leaveBudget);
        return;
      }
      if (!await _nextTickAsync(timer, cancellationToken).ConfigureAwait(false)) {
        return;
      }
    }
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // The timer exists before the first pass, so a tick that comes due while a pass runs is never missed.
    using var timer = new PeriodicTimer(Interval, _time);
    do {
      try {
        _ = await HandOverAsync(stoppingToken).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        // A failed pass is reported and retried on the next; past its deadline the assigner completes the handover.
        LogPassFailed(_logger, ex);
      }
    } while (await _nextTickAsync(timer, stoppingToken).ConfigureAwait(false));
  }

  // False once the host stops.
  private static async Task<bool> _nextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken) {
    try {
      return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return false;
    }
  }

  /// <summary>One pass: hands over every partition whose work is done, and returns the partitions flipped.</summary>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The partitions this pass flipped to their next owners.</returns>
  public async Task<IReadOnlyList<int>> HandOverAsync(CancellationToken cancellationToken) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var handingOver = await coordinator.GetPartitionsHandingOverAsync(_instance.InstanceId, cancellationToken).ConfigureAwait(false);
    if (handingOver.Count == 0) {
      return [];
    }
    var present = _participants.ToList();
    var busy = present.SelectMany(p => p.StreamsInFlight).Select(_partitionOf).OfType<int>().ToHashSet();
    var ready = handingOver.Where(h => !busy.Contains(h.Partition)).ToList();
    if (ready.Count == 0) {
      return [];
    }
    var readyPartitions = ready.Select(h => h.Partition).ToHashSet();
    bool handsOver(Guid stream) => _partitionOf(stream) is { } partition && readyPartitions.Contains(partition);
    foreach (var participant in present) {
      await participant.SaveForHandoverAsync(handsOver, cancellationToken).ConfigureAwait(false);
    }
    // The finished batch's completions, and what the participants just saved, land under the old epoch before the
    // flip; written after it, they would be fenced and replayed by the new owner.
    if (_landed is not null) {
      await _landed.WaitForAllLandedAsync(cancellationToken).ConfigureAwait(false);
    }
    var flipped = await coordinator.AcknowledgeHandoverAsync(_instance.InstanceId, ready, cancellationToken).ConfigureAwait(false);
    // What this instance held there is no longer its to fence by.
    _tenures.ForgetPartitions(flipped);
    LogHandedOver(_logger, flipped.Count, handingOver.Count - ready.Count);
    return flipped;
  }

  // The ownership table's partition: a row's partition past the table is owned through its remainder.
  private int? _partitionOf(Guid stream) => _tenures.OfStream(stream)?.Partition % 10000;

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Handed over {Flipped} partition(s) to their next owners; {Waiting} still have work in flight")]
  private static partial void LogHandedOver(ILogger logger, int flipped, int waiting);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "Leaving: {Owned} partition(s) still owned and nothing more to hand over; deregistering")]
  private static partial void LogLeft(ILogger logger, int owned);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "Leaving: {Owned} partition(s) still owned after {Budget}; deregistering, and the assigner completes their moves")]
  private static partial void LogLeaveGaveUp(ILogger logger, int owned, TimeSpan budget);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "A partition handover pass failed; the next pass retries, and past its deadline the assigner completes the handover")]
  private static partial void LogPassFailed(ILogger logger, Exception exception);
}
