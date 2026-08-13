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
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityRepairLedgerTests.cs</tests>
public sealed class IntegrityRepairLedger(int maxEntries = IntegrityRepairLedger.DEFAULT_MAX_ENTRIES)
  : IIntegrityRepairLedger {
  private const int DEFAULT_MAX_ENTRIES = 10_000;
  /// <summary>Backoff doublings are capped here so the wait never overflows (base × 2^6).</summary>
  private const int MAX_BACKOFF_DOUBLINGS = 6;

  /// <summary>One divergent bucket — stream-level entries carry the stream id; type- or
  /// window-level entries use <see cref="Guid.Empty"/>.</summary>
  public readonly record struct DivergenceKey(Guid OriginServiceId, string? TenantScope, string EventType, Guid StreamId);

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
  /// (either side's digest moved — progress or fresh damage; this also resets the repair-attempt
  /// budget), or the cooldown elapsed since the last report. Records the sighting either way.
  /// </summary>
  public bool TryBeginReport(
      DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
      DateTimeOffset now, TimeSpan cooldown) {
    lock (_lock) {
      var entry = _getOrAdd(key, now);
      entry.LastTouched = now;
      var signatureChanged = entry.OriginLo != originLo || entry.OriginHi != originHi
        || entry.LocalLo != localLo || entry.LocalHi != localHi;
      if (signatureChanged) {
        entry.OriginLo = originLo;
        entry.OriginHi = originHi;
        entry.LocalLo = localLo;
        entry.LocalHi = localHi;
        entry.RepairAttempts = 0;
        entry.LastRepairAt = null;
      }
      if (entry.LastReportedAt is null || signatureChanged || now - entry.LastReportedAt >= cooldown) {
        entry.LastReportedAt = now;
        return true;
      }
      return false;
    }
  }

  /// <summary>
  /// True when a repair request should be SENT now: the first attempt goes immediately, each
  /// later attempt waits base × 2^(n-1) since the previous one, and past
  /// <paramref name="maxAttempts"/> the ladder stops climbing and flattens to its terminal
  /// cadence (base × 2^6). A bucket whose budget burned against an unreachable origin has a
  /// static signature — nothing resets it (see <see cref="TryBeginReport"/>) — so a permanent
  /// deny would shadow-ban a repairable deficit forever; the terminal cadence keeps convergence
  /// eventually-true at bounded cost. Records the attempt when true.
  /// </summary>
  public bool TryBeginRepair(DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts) {
    lock (_lock) {
      var entry = _getOrAdd(key, now);
      entry.LastTouched = now;
      if (entry.LastRepairAt is { } last) {
        var doublings = entry.RepairAttempts >= maxAttempts
          ? MAX_BACKOFF_DOUBLINGS
          : Math.Min(entry.RepairAttempts - 1, MAX_BACKOFF_DOUBLINGS);
        var wait = baseBackoff * Math.Pow(2, Math.Max(0, doublings));
        if (now - last < wait) {
          return false;
        }
      }
      entry.RepairAttempts++;
      entry.LastRepairAt = now;
      return true;
    }
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
