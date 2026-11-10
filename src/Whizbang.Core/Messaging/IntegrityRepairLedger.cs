// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Stream-integrity convergence state: remembers what divergence has already been REPORTED and
/// how often a bucket's REPAIR has been requested, so a persistent divergence — an origin that
/// is down, or a genuinely damaged bucket — produces a bounded trickle instead of a storm.
/// Without this memory the audit re-reports and re-requests every divergent bucket on every
/// cycle; observed live, that flood alone saturated a shared database server and kept the very
/// origin that could heal the divergence from ever starting.
/// In-memory by design (like <see cref="IntegrityGapTracker"/>): a restart re-reports and
/// re-requests once, then the ledger re-bounds — over-reporting after a restart is the safe
/// failure mode. Hard-bounded: the oldest-touched entry is evicted at capacity.
/// Its repair ladder is the durable ledger's, held to it by one shared contract test: a real attempt cap, a pending
/// answer with its timeout, resets only on a moved local digest or a grown deficit, and the type-lane stand-down.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityRepairLedgerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryIntegrityRepairLedgerContractTests.cs</tests>
public sealed class IntegrityRepairLedger(int maxEntries = IntegrityRepairLedger.DEFAULT_MAX_ENTRIES)
  : IIntegrityRepairLedger {
  private const int DEFAULT_MAX_ENTRIES = 10_000;
  /// <summary>Backoff doublings are capped here so the wait never overflows (base × 2^6).</summary>
  private const int MAX_BACKOFF_DOUBLINGS = 6;

  /// <summary>One divergent bucket — stream-level entries carry the stream id; type- or
  /// window-level entries use <see cref="Guid.Empty"/>.</summary>
  public readonly record struct DivergenceKey(Guid OriginServiceId, string? TenantScope, string EventType, Guid StreamId) {
    /// <summary>The same (origin, tenant, type) bucket at the type level: its stream id is <see cref="Guid.Empty"/>.</summary>
    /// <docs>resilience/stream-integrity#a-matching-type-heals-its-type-lane</docs>
    /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityRepairLedgerTypeHealTests.cs</tests>
    public DivergenceKey TypeLane => new(OriginServiceId, TenantScope, EventType, Guid.Empty);
  }

  private sealed class Entry {
    public long OriginLo;
    public long OriginHi;
    public long LocalLo;
    public long LocalHi;
    public DateTimeOffset? LastReportedAt;
    public DateTimeOffset? LastRepairAt;
    public int RepairAttempts;
    public DateTimeOffset LastTouched;
    public DateTimeOffset FirstSeen;
    /// <summary>When a redelivery showed repair cannot change this bucket; null while it still can.</summary>
    public DateTimeOffset? UnhealableAt;
    /// <summary>The answer to the last request is on its way until then (#2029).</summary>
    public DateTimeOffset? AnswerPendingUntil;
    /// <summary>The bucket spent its attempt budget (#2029).</summary>
    public bool Exhausted;
    /// <summary>The deficit, in events, the last sized report recorded; null when unknown (#2030).</summary>
    public long? DeficitEvents;
  }

  private readonly Lock _lock = new();
  private readonly Dictionary<DivergenceKey, Entry> _entries = [];

  /// <summary>Entries currently tracked (test visibility for the bound).</summary>
  public int Count {
    get {
      lock (_lock) {
        return _entries.Count;
      }
    }
  }

  /// <summary>
  /// True when this divergence should be REPORTED now: first sighting, a changed signature
  /// (either side's digest moved), or the cooldown elapsed since the last report. Records the sighting either way.
  /// Only a change in the LOCAL digest resets the repair ladder (#2030): the attempt budget, the unhealable and
  /// exhausted marks and a pending answer. The origin's digest moves with every event it appends, which alone says
  /// nothing about what a redelivery can do here. Any sighting clears the answered state.
  /// </summary>
  public bool TryBeginReport(
      DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
      DateTimeOffset now, TimeSpan cooldown) {
    lock (_lock) {
      // The ladder reads the digests and the deficit size only; whether it is a deficit does not change the report.
      return _report(new IntegrityReportObservation(key, originLo, originHi, localLo, localHi, IsDeficit: false), now, cooldown);
    }
  }

  /// <inheritdoc />
  /// <remarks>One lock for the whole batch, and each observation's deficit size is recorded: a deficit that grew past
  /// the recorded one resets the ladder as a local change does (#2030); an unknown size keeps the recorded one.</remarks>
  public ValueTask<IReadOnlyList<bool>> TryBeginReportBatchAsync(
      IReadOnlyList<IntegrityReportObservation> observations, DateTimeOffset now, TimeSpan cooldown,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(observations);
    var results = new bool[observations.Count];
    lock (_lock) {
      for (var i = 0; i < observations.Count; i++) {
        results[i] = _report(observations[i], now, cooldown);
      }
    }
    return ValueTask.FromResult<IReadOnlyList<bool>>(results);
  }

  private bool _report(IntegrityReportObservation observation, DateTimeOffset now, TimeSpan cooldown) {
    var (key, originLo, originHi, localLo, localHi, _, deficitEvents) = observation;
    var entry = _getOrAdd(key, now);
    entry.LastTouched = now;
    var localChanged = entry.LocalLo != localLo || entry.LocalHi != localHi;
    var signatureChanged = localChanged || entry.OriginLo != originLo || entry.OriginHi != originHi;
    var deficitGrew = deficitEvents > entry.DeficitEvents;
    entry.OriginLo = originLo;
    entry.OriginHi = originHi;
    entry.LocalLo = localLo;
    entry.LocalHi = localHi;
    entry.DeficitEvents = deficitEvents ?? entry.DeficitEvents;
    if (localChanged || deficitGrew) {
      entry.RepairAttempts = 0;
      entry.LastRepairAt = null;
      entry.UnhealableAt = null;
      entry.Exhausted = false;
      entry.AnswerPendingUntil = null;
    }
    // A bucket repair cannot change was reported once, when it was marked; the cooldown does not repeat it.
    if (entry.LastReportedAt is not { } lastReportedAt || signatureChanged
        || (entry.UnhealableAt is null && now - lastReportedAt >= cooldown)) {
      entry.LastReportedAt = now;
      return true;
    }
    return false;
  }

  /// <summary>
  /// True when a repair request should be SENT now: the first attempt goes immediately, each later attempt waits
  /// base × 2^(n-1) since the previous one (6 doublings at most), and the grant that spends
  /// <paramref name="maxAttempts"/> exhausts the bucket: it is never granted again until a reset (#2029), a moved local
  /// digest or a deficit that grew (<see cref="TryBeginReport"/>). Refused while the answer to the last request is
  /// pending (<see cref="MarkAnswerPendingAsync"/>), for a bucket a redelivery showed repair cannot change
  /// (<see cref="MarkUnhealableBatchAsync"/>), and on the type lane (<see cref="Guid.Empty"/> stream) while per-stream
  /// requests of its type await their answers (#2034). Records the attempt when true.
  /// </summary>
  public bool TryBeginRepair(DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts) {
    lock (_lock) {
      if (key.StreamId == Guid.Empty && _entries.Any(kv =>
            kv.Key.StreamId != Guid.Empty && kv.Value.AnswerPendingUntil > now && _sameType(kv.Key, key))) {
        return false;   // the per-stream requests already re-ship the type's events
      }
      var entry = _getOrAdd(key, now);
      entry.LastTouched = now;
      if (entry.UnhealableAt is not null || entry.Exhausted || entry.RepairAttempts >= maxAttempts
          || entry.AnswerPendingUntil > now) {
        return false;
      }
      if (entry.LastRepairAt is { } last) {
        var doublings = Math.Clamp(entry.RepairAttempts - 1, 0, MAX_BACKOFF_DOUBLINGS);
        if (now - last < baseBackoff * Math.Pow(2, doublings)) {
          return false;
        }
      }
      entry.RepairAttempts++;
      entry.LastRepairAt = now;
      entry.Exhausted = entry.RepairAttempts >= maxAttempts;
      return true;
    }
  }

  /// <inheritdoc />
  /// <remarks>Per-stream buckets here carry no compared window (only the durable store stamps one), so a type-lane
  /// mark with a window covers none of them, as the durable store treats its unstamped rows; without a window it
  /// covers every tracked bucket of the type.</remarks>
  public ValueTask<int> MarkAnswerPendingAsync(
      IReadOnlyList<DivergenceKey> keys, long? windowFrom, long? windowUntil, DateTimeOffset now, TimeSpan timeout,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(keys);
    var unbounded = windowFrom is null || windowUntil is null;
    var marked = 0;
    lock (_lock) {
      foreach (var (entryKey, entry) in _entries) {
        if (keys.Any(k => _sameType(k, entryKey) && (k.StreamId == entryKey.StreamId || (k.StreamId == Guid.Empty && unbounded)))) {
          entry.AnswerPendingUntil = now + timeout;
          entry.LastTouched = now;
          marked++;
        }
      }
    }
    return ValueTask.FromResult(marked);
  }

  /// <inheritdoc />
  /// <remarks>Here the answered state is the pending answer cleared. The durable ledger also keeps an answered mark,
  /// but only its paced drain reads it, and this ledger has no drain: both grant paths treat an answered bucket alike.</remarks>
  public ValueTask MarkAnsweredAsync(IReadOnlyList<DivergenceKey> keys, DateTimeOffset now, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(keys);
    lock (_lock) {
      foreach (var key in keys) {
        if (_entries.TryGetValue(key, out var entry)) {
          entry.AnswerPendingUntil = null;
          entry.LastTouched = now;
        }
      }
    }
    return ValueTask.CompletedTask;
  }

  // Same origin, tenant and type, whatever the stream. The durable ledger keys an absent tenant as '', so a null and an
  // empty tenant are the same bucket; type names compare in their normalized form (ai-docs/type-naming.md).
  private static bool _sameType(DivergenceKey a, DivergenceKey b) {
    if (a.OriginServiceId != b.OriginServiceId
        || !string.Equals(a.TenantScope ?? string.Empty, b.TenantScope ?? string.Empty, StringComparison.Ordinal)) {
      return false;
    }
    var left = EventTypeMatchingHelper.NormalizeTypeName(a.EventType);
    var right = EventTypeMatchingHelper.NormalizeTypeName(b.EventType);
    return string.Equals(left, right, StringComparison.Ordinal);
  }

  /// <inheritdoc />
  public ValueTask<bool> TryBeginReportAsync(
      DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
      DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(TryBeginReport(key, originLo, originHi, localLo, localHi, now, cooldown));

  /// <inheritdoc />
  public ValueTask<bool> TryBeginRepairAsync(
      DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
      CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(TryBeginRepair(key, now, baseBackoff, maxAttempts));

  /// <inheritdoc />
  public ValueTask MarkHealedAsync(DivergenceKey key, CancellationToken cancellationToken = default) {
    MarkHealed(key);
    return ValueTask.CompletedTask;
  }

  /// <summary>The bucket folded identical — forget it. A later divergence is a brand-new incident.</summary>
  public void MarkHealed(DivergenceKey key) {
    lock (_lock) {
      _entries.Remove(key);
    }
  }

  /// <inheritdoc />
  /// <remarks>Ages come straight off the entries this heal removes — the first-seen clock was
  /// already there for the oldest-unhealed gauge; destroying the entry reads it back for free.</remarks>
  public ValueTask<IReadOnlyList<double>> MarkHealedBatchWithAgesAsync(
      IReadOnlyList<DivergenceKey> keys, CancellationToken cancellationToken = default) {
    var now = DateTimeOffset.UtcNow;
    var ages = new List<double>(keys.Count);
    lock (_lock) {
      foreach (var key in keys) {
        if (_entries.Remove(key, out var entry)) {
          ages.Add(Math.Max(0d, (now - entry.FirstSeen).TotalSeconds));
        }
      }
    }
    return ValueTask.FromResult<IReadOnlyList<double>>(ages);
  }

  /// <inheritdoc />
  /// <remarks>Every entry of the proven (origin, tenant, type) triples goes, whatever its stream; ages come off the
  /// entries removed, as for <see cref="MarkHealedBatchWithAgesAsync"/>.</remarks>
  public ValueTask<IReadOnlyList<double>> MarkTypesHealedWithAgesAsync(
      IReadOnlyList<DivergenceKey> typeKeys, CancellationToken cancellationToken = default) {
    var proven = typeKeys.Select(k => (k.OriginServiceId, k.TenantScope, k.EventType)).ToHashSet();
    var now = DateTimeOffset.UtcNow;
    var ages = new List<double>();
    lock (_lock) {
      foreach (var key in _entries.Keys.Where(k => proven.Contains((k.OriginServiceId, k.TenantScope, k.EventType))).ToList()) {
        _entries.Remove(key, out var entry);
        ages.Add(Math.Max(0d, (now - entry!.FirstSeen).TotalSeconds));
      }
    }
    return ValueTask.FromResult<IReadOnlyList<double>>(ages);
  }

  /// <inheritdoc />
  /// <remarks>Marks only buckets this ledger tracks: a redelivery nobody asked this ledger for leaves nothing
  /// behind. A changed signature (<see cref="TryBeginReport"/>) or a heal lifts the mark.</remarks>
  public ValueTask<IReadOnlyList<DivergenceKey>> MarkUnhealableBatchAsync(
      IReadOnlyList<DivergenceKey> keys, DateTimeOffset now, CancellationToken cancellationToken = default) {
    var marked = new List<DivergenceKey>();
    lock (_lock) {
      foreach (var key in keys) {
        if (_entries.TryGetValue(key, out var entry) && entry.UnhealableAt is null) {
          entry.UnhealableAt = now;
          entry.LastTouched = now;
          marked.Add(key);
        }
      }
    }
    return ValueTask.FromResult<IReadOnlyList<DivergenceKey>>(marked);
  }

  /// <inheritdoc />
  /// <remarks>This ledger keeps no compared window, so every bucket here is whole-history: only a whole-history answer
  /// covers it (#2143), and it keeps no named missing event either. A windowed answer marks nothing.</remarks>
  public ValueTask<IReadOnlyList<DivergenceKey>> MarkUnhealableBatchAsync(
      IReadOnlyList<DivergenceKey> keys, RedeliveryAnswerWindow answeredWindow, IReadOnlyList<Guid> answeredEventIds,
      DateTimeOffset now, CancellationToken cancellationToken = default) =>
    answeredWindow.IsWholeHistory
      ? MarkUnhealableBatchAsync(keys, now, cancellationToken)
      : ValueTask.FromResult<IReadOnlyList<DivergenceKey>>([]);

  private Entry _getOrAdd(DivergenceKey key, DateTimeOffset now) {
    if (_entries.TryGetValue(key, out var entry)) {
      return entry;
    }
    if (_entries.Count >= maxEntries) {
      var oldest = _entries.MinBy(kv => kv.Value.LastTouched);
      _entries.Remove(oldest.Key);
    }
    entry = new Entry {
      // A sentinel signature no real digest pair produces, so the first TryBeginReport always
      // sees "changed" and both paths treat the entry as fresh.
      OriginLo = long.MinValue,
      OriginHi = long.MinValue,
      LocalLo = long.MinValue,
      LocalHi = long.MinValue,
      LastTouched = now,
      FirstSeen = now,
    };
    _entries[key] = entry;
    return entry;
  }
}
