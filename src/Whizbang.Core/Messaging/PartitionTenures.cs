// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>A partition and the epoch at which this instance held it when it claimed work there (#2556).</summary>
/// <param name="Partition">The partition number.</param>
/// <param name="Epoch">The ownership epoch; -1 when the claim was made without owning the partition.</param>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
public readonly record struct PartitionTenure(int Partition, long Epoch);

/// <summary>A partition this instance owns and is handing over to <paramref name="NextOwner"/> (#2557).</summary>
/// <param name="Partition">The partition number.</param>
/// <param name="Epoch">The epoch it is handed over at, which its acknowledgement names.</param>
/// <param name="NextOwner">The instance it goes to.</param>
/// <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
public readonly record struct PartitionHandover(int Partition, long Epoch, Guid NextOwner);

/// <summary>What a leaving instance still owns (#2557, graceful leave).</summary>
/// <param name="Owned">The partitions it owns, in either state; zero off map mode.</param>
/// <param name="OtherLiveCandidates">The other live instances that could take them.</param>
/// <docs>fundamentals/work-coordinator/partition-assignment#graceful-leave</docs>
public readonly record struct PartitionLeaveStatus(int Owned, int OtherLiveCandidates);

/// <summary>
/// The process's ledger of partition tenures (#2556): the tenure each claimed stream was claimed under, and the tenure
/// each fetched piece of work was fetched under. The work coordinator records into it on every claim and fetch and reads
/// it on every write that commits a result, so a completion, failure, cursor or handler commit names the epoch its work
/// was claimed under without any worker carrying it, and lands nowhere once the partition's ownership has moved on.
/// </summary>
/// <remarks>
/// <para>
/// A work id keeps the tenure it was fetched under even when a later claim records a newer one for its stream, so work
/// fetched under an epoch is written under that epoch: a partition that moves away and back cannot make it current
/// again. Off map mode no claim carries an epoch, nothing is recorded, and nothing is fenced.
/// </para>
/// <para>
/// Bounded: entries go into the current generation, and once it holds <c>capacity</c> entries it becomes the previous
/// one and the one before is dropped. A lookup reads both. The working set of claimed and in-flight work is far below
/// the default capacity, so only entries long finished are ever dropped; one that is dropped names no epoch and is not
/// fenced, as a write that never claimed is not.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/PartitionTenuresTests.cs</tests>
public sealed class PartitionTenures {
  /// <summary>The default number of entries a generation holds.</summary>
  public const int DEFAULT_CAPACITY = 250_000;

  private readonly int _capacity;
  private readonly Lock _gate = new();
  private Dictionary<Guid, PartitionTenure> _streams = [];
  private Dictionary<Guid, PartitionTenure> _previousStreams = [];
  private Dictionary<Guid, PartitionTenure> _work = [];
  private Dictionary<Guid, PartitionTenure> _previousWork = [];

  /// <summary>Creates a ledger.</summary>
  /// <param name="capacity">The entries a generation holds before it is rotated.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
  public PartitionTenures(int capacity = DEFAULT_CAPACITY) {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
    _capacity = capacity;
  }

  /// <summary>Records the tenures a claim returned: each stream's epoch, with its partition.</summary>
  /// <param name="epochs">The claim's epochs by stream (<see cref="WorkBatch.StreamEpochs"/>).</param>
  /// <param name="partitions">The claim's partitions by stream (<see cref="WorkBatch.StreamPartitions"/>).</param>
  public void RecordClaim(IReadOnlyDictionary<Guid, long> epochs, IReadOnlyDictionary<Guid, int> partitions) {
    ArgumentNullException.ThrowIfNull(epochs);
    ArgumentNullException.ThrowIfNull(partitions);
    lock (_gate) {
      foreach (var (stream, epoch) in epochs) {
        if (partitions.TryGetValue(stream, out var partition)) {
          _put(ref _streams, ref _previousStreams, stream, new PartitionTenure(partition, epoch));
        }
      }
    }
  }

  /// <summary>Records a fetched piece of work under its stream's current tenure; nothing when the stream has none.</summary>
  /// <param name="workId">The message id or perspective work id.</param>
  /// <param name="streamId">Its stream.</param>
  public void RecordFetched(Guid workId, Guid? streamId) {
    lock (_gate) {
      if (streamId is { } stream && _read(_streams, _previousStreams, stream) is { } tenure) {
        _put(ref _work, ref _previousWork, workId, tenure);
      }
    }
  }

  /// <summary>The tenure a stream was last claimed under, or null.</summary>
  /// <param name="streamId">The stream.</param>
  /// <returns>Its tenure, or null.</returns>
  public PartitionTenure? OfStream(Guid streamId) {
    lock (_gate) {
      return _read(_streams, _previousStreams, streamId);
    }
  }

  /// <summary>The tenure a piece of work was fetched under, or null.</summary>
  /// <param name="workId">The message id or perspective work id.</param>
  /// <returns>Its tenure, or null.</returns>
  public PartitionTenure? OfWork(Guid workId) {
    lock (_gate) {
      return _read(_work, _previousWork, workId);
    }
  }

  /// <summary>Forgets work whose result has been written.</summary>
  /// <param name="workIds">The work ids.</param>
  public void Forget(IEnumerable<Guid> workIds) {
    ArgumentNullException.ThrowIfNull(workIds);
    lock (_gate) {
      foreach (var id in workIds) {
        _work.Remove(id);
        _previousWork.Remove(id);
      }
    }
  }

  /// <summary>
  /// Forgets every tenure in partitions this instance has handed over (#2557), so nothing it does later on those streams,
  /// a rebuild included, is judged by a tenure it no longer holds.
  /// </summary>
  /// <param name="partitions">The ownership table's partitions (0 to 9,999) handed over.</param>
  public void ForgetPartitions(IReadOnlyCollection<int> partitions) {
    ArgumentNullException.ThrowIfNull(partitions);
    if (partitions.Count == 0) {
      return;
    }
    var set = partitions as IReadOnlySet<int> ?? partitions.ToHashSet();
    lock (_gate) {
      foreach (var map in new[] { _streams, _previousStreams, _work, _previousWork }) {
        foreach (var key in map.Where(e => set.Contains(e.Value.Partition % 10000)).Select(e => e.Key).ToList()) {
          map.Remove(key);
        }
      }
    }
  }

  private void _put(ref Dictionary<Guid, PartitionTenure> current, ref Dictionary<Guid, PartitionTenure> previous, Guid key, PartitionTenure value) {
    if (current.Count >= _capacity && !current.ContainsKey(key)) {
      previous = current;
      current = [];
    }
    current[key] = value;
  }

  private static PartitionTenure? _read(Dictionary<Guid, PartitionTenure> current, Dictionary<Guid, PartitionTenure> previous, Guid key) =>
    current.TryGetValue(key, out var tenure) || previous.TryGetValue(key, out tenure) ? tenure : null;
}
