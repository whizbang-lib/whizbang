// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Workers;

/// <summary>Why a held model is flushed.</summary>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
internal enum WriteBehindFlushReason {
  /// <summary>The model has been held for the flush interval.</summary>
  Interval = 0,

  /// <summary>The model holds the most batches one model may hold.</summary>
  Batches = 1,

  /// <summary>The cache is over its model or byte bound, and this is among its oldest models.</summary>
  Bounds = 2,

  /// <summary>Something outside the held batches needs the row current: a rewind, a per-pair apply, a drain, a purge.</summary>
  Bypass = 3,

  /// <summary>A sync wait is waiting on an event the model holds.</summary>
  Sync = 4,

  /// <summary>The worker is stopping.</summary>
  Stop = 5,

  /// <summary>A caller asked for every held model to be flushed.</summary>
  Requested = 6,
}

/// <summary>One batch a write-behind model holds: what the flush writes and completes for it.</summary>
/// <typeparam name="THeld">What the worker keeps for the batch until the flush.</typeparam>
/// <param name="Item">The worker's held batch.</param>
/// <param name="Write">The batch's prepared write.</param>
/// <param name="Row">The model after the batch.</param>
/// <param name="EventIds">The events the batch folded.</param>
internal sealed record WriteBehindHold<THeld>(
    THeld Item, PendingPerspectiveWrite Write, PerspectiveWriteBehindRow Row, IReadOnlyList<Guid> EventIds) where THeld : class;

/// <summary>One held model: its batches in the order they were folded, and when it was first held.</summary>
/// <typeparam name="THeld">What the worker keeps for each batch.</typeparam>
internal sealed class WriteBehindEntry<THeld> where THeld : class {
  private readonly List<WriteBehindHold<THeld>> _holds = [];
  private readonly HashSet<Guid> _eventIds = [];

  internal WriteBehindEntry(DateTimeOffset firstHeldAt) => FirstHeldAt = firstHeldAt;

  /// <summary>When the first batch was held, which the flush interval and the lease are counted from.</summary>
  public DateTimeOffset FirstHeldAt { get; }

  /// <summary>The batches, oldest first.</summary>
  public IReadOnlyList<WriteBehindHold<THeld>> Holds => _holds;

  /// <summary>The model after the last batch.</summary>
  public PerspectiveWriteBehindRow Row => _holds[^1].Row;

  /// <summary>Every event the held batches folded.</summary>
  public IReadOnlySet<Guid> EventIds => _eventIds;

  /// <summary>The one write that writes every batch: the first write followed by each later one.</summary>
  public PendingPerspectiveWrite MergedWrite() {
    var merged = _holds[0].Write;
    for (var i = 1; i < _holds.Count; i++) {
      merged = merged.FollowedBy(_holds[i].Write);
    }
    return merged;
  }

  internal void Add(WriteBehindHold<THeld> hold) {
    _holds.Add(hold);
    _eventIds.UnionWith(hold.EventIds);
  }
}

/// <summary>
/// The models the write-behind mode holds (#2247), keyed by (stream, perspective): bounded by model count and bytes, and
/// asked which models are due a flush and which have outlived the lease their rows were claimed with.
/// </summary>
/// <remarks>
/// <para>
/// A key's entry is read and changed only by whoever holds the key's affinity gate; the cache's own lock keeps its
/// totals and its view of every key consistent for the questions asked across keys (what is due, what holds an event).
/// </para>
/// <para>
/// A new model is held only when it fits both bounds, so the cache never grows past them by adding a model; a held
/// model's later batch is always added (its write merges into the held one), and the bound is restored by flushing the
/// oldest models (<see cref="Due"/>).
/// </para>
/// </remarks>
/// <typeparam name="THeld">What the worker keeps for each held batch.</typeparam>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/PerspectiveWriteBehindCacheTests.cs</tests>
internal sealed class PerspectiveWriteBehindCache<THeld> where THeld : class {
  private readonly Lock _lock = new();
  private readonly Dictionary<(Guid StreamId, string PerspectiveName), WriteBehindEntry<THeld>> _entries = [];
  private readonly int _maxModels;
  private readonly long _maxBytes;
  private readonly int _maxBatches;
  private readonly TimeSpan _interval;
  private readonly TimeSpan _leaseLimit;
  private long _bytes;

  /// <summary>Creates a cache with the given bounds.</summary>
  /// <param name="options">The bounds; <see cref="PerspectiveWriteBehindOptions.MaxModels"/> and <see cref="PerspectiveWriteBehindOptions.MaxBytes"/> are required.</param>
  /// <param name="leaseLimit">How long after its first hold a model's rows are still leased to this instance.</param>
  public PerspectiveWriteBehindCache(PerspectiveWriteBehindOptions options, TimeSpan leaseLimit) {
    ArgumentNullException.ThrowIfNull(options);
    _maxModels = options.MaxModels ?? throw new ArgumentException("Write-behind needs MaxModels.", nameof(options));
    _maxBytes = options.MaxBytes ?? throw new ArgumentException("Write-behind needs MaxBytes.", nameof(options));
    _maxBatches = options.MaxBatchesPerModel;
    _interval = TimeSpan.FromMilliseconds(options.MaxFlushIntervalMilliseconds);
    _leaseLimit = leaseLimit;
  }

  /// <summary>How many models are held.</summary>
  public int Count {
    get {
      lock (_lock) {
        return _entries.Count;
      }
    }
  }

  /// <summary>How many bytes the held models take.</summary>
  public long Bytes {
    get {
      lock (_lock) {
        return _bytes;
      }
    }
  }

  /// <summary>The model held for the key, or null.</summary>
  public WriteBehindEntry<THeld>? Get((Guid StreamId, string PerspectiveName) key) {
    lock (_lock) {
      return _entries.GetValueOrDefault(key);
    }
  }

  /// <summary>
  /// Holds a batch: added to its key's model when one is held, else held as a new model when it fits both bounds.
  /// </summary>
  /// <returns>False when a new model does not fit, and the batch is to be written now.</returns>
  public bool TryHold((Guid StreamId, string PerspectiveName) key, WriteBehindHold<THeld> hold, DateTimeOffset now) {
    ArgumentNullException.ThrowIfNull(hold);
    lock (_lock) {
      if (_entries.TryGetValue(key, out var entry)) {
        _bytes += hold.Row.Bytes - entry.Row.Bytes;
        entry.Add(hold);
        return true;
      }
      if (_entries.Count + 1 > _maxModels || _bytes + hold.Row.Bytes > _maxBytes) {
        return false;
      }
      entry = new WriteBehindEntry<THeld>(now);
      entry.Add(hold);
      _entries[key] = entry;
      _bytes += hold.Row.Bytes;
      return true;
    }
  }

  /// <summary>Removes and returns the key's model, or null when none is held.</summary>
  public WriteBehindEntry<THeld>? Take((Guid StreamId, string PerspectiveName) key) {
    lock (_lock) {
      if (!_entries.Remove(key, out var entry)) {
        return null;
      }
      _bytes -= entry.Row.Bytes;
      return entry;
    }
  }

  /// <summary>Every held key.</summary>
  public IReadOnlyList<(Guid StreamId, string PerspectiveName)> Keys() {
    lock (_lock) {
      return [.. _entries.Keys];
    }
  }

  /// <summary>The held keys whose models folded any of <paramref name="eventIds"/>.</summary>
  public IReadOnlyList<(Guid StreamId, string PerspectiveName)> KeysHolding(IReadOnlyCollection<Guid> eventIds) {
    ArgumentNullException.ThrowIfNull(eventIds);
    lock (_lock) {
      return [.. _entries.Where(e => eventIds.Any(e.Value.EventIds.Contains)).Select(e => e.Key)];
    }
  }

  /// <summary>Whether a held model's rows may no longer be this instance's at <paramref name="now"/>, so it must not be written.</summary>
  public bool HasOutlivedLease(WriteBehindEntry<THeld> entry, DateTimeOffset now) {
    ArgumentNullException.ThrowIfNull(entry);
    return now - entry.FirstHeldAt >= _leaseLimit;
  }

  /// <summary>
  /// What is due at <paramref name="now"/>: the models past the flush interval or the batch cap, the oldest models
  /// while the cache is over a bound, and apart from them the models past the lease, which are dropped, not flushed.
  /// </summary>
  public WriteBehindDue Due(DateTimeOffset now) {
    lock (_lock) {
      var expired = new List<(Guid StreamId, string PerspectiveName)>();
      var flush = new List<((Guid StreamId, string PerspectiveName) Key, WriteBehindFlushReason Reason)>();
      var count = _entries.Count;
      var bytes = _bytes;
      foreach (var (key, entry) in _entries.OrderBy(e => e.Value.FirstHeldAt)) {
        if (HasOutlivedLease(entry, now)) {
          expired.Add(key);
        } else if (count > _maxModels || bytes > _maxBytes) {
          flush.Add((key, WriteBehindFlushReason.Bounds));
        } else if (entry.Holds.Count >= _maxBatches) {
          flush.Add((key, WriteBehindFlushReason.Batches));
        } else if (now - entry.FirstHeldAt >= _interval) {
          flush.Add((key, WriteBehindFlushReason.Interval));
        } else {
          continue;
        }
        count--;
        bytes -= entry.Row.Bytes;
      }
      return new WriteBehindDue(flush, expired);
    }
  }
}

/// <summary>What a write-behind cache has due: the models to flush, each with its reason, and the models to drop.</summary>
/// <param name="Flush">The models to flush.</param>
/// <param name="Expired">The models whose lease has run out, dropped without a write.</param>
internal readonly record struct WriteBehindDue(
    IReadOnlyList<((Guid StreamId, string PerspectiveName) Key, WriteBehindFlushReason Reason)> Flush,
    IReadOnlyList<(Guid StreamId, string PerspectiveName)> Expired);
