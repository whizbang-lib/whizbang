namespace Whizbang.Core.Observability;

/// <summary>
/// The per-process advisory ledger: what this instance has already said, for as long as it lives.
/// </summary>
/// <remarks>
/// <para>
/// The default, so a host with no store still gets advice exactly once rather than every cycle. It
/// is also the fallback a durable ledger degrades to when the database cannot be reached, which is
/// why it is a real implementation of the cooldown rather than a bare set of keys: degrading has to
/// leave the semantics intact and only shorten the memory.
/// </para>
/// <para>
/// Bounded, because it is reached from a cycle that runs forever and the set of findings is
/// whatever the deployed schema produces. Past the ceiling the least recently reported entry is
/// dropped, which at worst repeats one piece of advice.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/AdvisoryLedgerTests.cs</tests>
public sealed class AdvisoryLedger : IAdvisoryLedger {

  /// <summary>How many findings this remembers before it starts forgetting the oldest.</summary>
  /// <remarks>
  /// One entry per table that has ever been advised about. A service with more than this many
  /// oversized tables has a bigger problem than duplicate advice.
  /// </remarks>
#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  public const int CAPACITY = 1024;
#pragma warning restore CA1707

  private readonly Lock _gate = new();
  private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

  private sealed record Entry(string Signature, DateTimeOffset ReportedAt);

  /// <inheritdoc />
  public ValueTask<bool> TryBeginReportAsync(
      string findingKey, string signature, DateTimeOffset now, TimeSpan cooldown,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(findingKey);
    ArgumentNullException.ThrowIfNull(signature);
    cancellationToken.ThrowIfCancellationRequested();

    lock (_gate) {
      if (_entries.TryGetValue(findingKey, out var seen)
          && string.Equals(seen.Signature, signature, StringComparison.Ordinal)
          && now - seen.ReportedAt < cooldown) {
        return ValueTask.FromResult(false);
      }

      if (_entries.Count >= CAPACITY && !_entries.ContainsKey(findingKey)) {
        _evictOldest();
      }

      _entries[findingKey] = new Entry(signature, now);
      return ValueTask.FromResult(true);
    }
  }

  /// <summary>Caller holds the gate.</summary>
  private void _evictOldest() {
    var oldestKey = (string?)null;
    var oldestAt = DateTimeOffset.MaxValue;

    foreach (var (key, entry) in _entries) {
      if (entry.ReportedAt <= oldestAt) {
        oldestAt = entry.ReportedAt;
        oldestKey = key;
      }
    }

    if (oldestKey is not null) {
      _entries.Remove(oldestKey);
    }
  }
}
