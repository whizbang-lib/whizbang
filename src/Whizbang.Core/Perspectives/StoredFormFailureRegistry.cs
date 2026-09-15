using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The streams whose stored document a perspective could not read, remembered once each until
/// they read again, so the error fires once per stream and the health endpoint can count them.
/// </summary>
/// <remarks>
/// <para>
/// The rows themselves are parked in the database: the worker reports each leased row of a
/// stream it cannot read through the failure channel, and the failure function records the
/// failure, schedules the retry with backoff, and dead-letters the row at the configured
/// threshold. This registry is only what the process remembers about it: enough to say the error
/// once instead of every cycle, and to report the count while it lasts.
/// </para>
/// <para>
/// In-process and bounded. A stream that reads again is released. A stream that is never heard
/// of again, because its rows were dead-lettered, is forgotten after <see cref="ForgetAfter"/>,
/// so the registry does not grow with every such stream for the life of the process.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/StoredFormFailureRegistryTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathDrainTests.StoredForm.cs</tests>
public sealed class StoredFormFailureRegistry {
  /// <summary>What is remembered about one perspective and stream.</summary>
  /// <param name="PerspectiveName">The perspective that could not read.</param>
  /// <param name="StreamId">The stream whose document it could not read.</param>
  /// <param name="Path">The JSON path of the value refused, when the reader knew it.</param>
  /// <param name="Detail">The refusal, naming the type, the forms accepted and the token found.</param>
  /// <param name="Failures">How many times it has failed since it last read.</param>
  /// <param name="LastFailedAt">When it last failed.</param>
  public sealed record Entry(
    string PerspectiveName,
    Guid StreamId,
    string? Path,
    string Detail,
    int Failures,
    DateTimeOffset LastFailedAt);

  private readonly ConcurrentDictionary<(string PerspectiveName, Guid StreamId), Entry> _entries = new();
  private readonly TimeProvider _time;

  /// <summary>Creates the registry.</summary>
  /// <param name="timeProvider">The clock, or the system clock.</param>
  public StoredFormFailureRegistry(TimeProvider? timeProvider = null) {
    _time = timeProvider ?? TimeProvider.System;
  }

  /// <summary>How long a stream nothing has been heard of stays in the registry. Default one hour.</summary>
  public TimeSpan ForgetAfter { get; init; } = TimeSpan.FromHours(1);

  /// <summary>How many perspective and stream pairs are currently remembered.</summary>
  public int Count {
    get {
      _forgetStale();
      return _entries.Count;
    }
  }

  /// <summary>
  /// Records a failure to read. The entry returned says whether it is the first since the stream
  /// last read (<see cref="Entry.Failures"/> is one) or a repeat.
  /// </summary>
  /// <param name="perspectiveName">The perspective that could not read.</param>
  /// <param name="streamId">The stream whose document it could not read.</param>
  /// <param name="failure">The classified failure.</param>
  /// <returns>The entry as it now stands.</returns>
  public Entry Record(string perspectiveName, Guid streamId, StoredFormUnreadable failure) {
    ArgumentNullException.ThrowIfNull(perspectiveName);
    ArgumentNullException.ThrowIfNull(failure);
    _forgetStale();
    var now = _time.GetUtcNow();
    return _entries.AddOrUpdate(
      (perspectiveName, streamId),
      _ => new Entry(perspectiveName, streamId, failure.Path, failure.Detail, 1, now),
      (_, existing) => existing with { Path = failure.Path, Detail = failure.Detail, Failures = existing.Failures + 1, LastFailedAt = now });
  }

  /// <summary>Releases a stream that read again.</summary>
  /// <param name="perspectiveName">The perspective that read.</param>
  /// <param name="streamId">The stream it read.</param>
  /// <returns><see langword="true"/> when the stream was remembered.</returns>
  public bool Recovered(string perspectiveName, Guid streamId) {
    ArgumentNullException.ThrowIfNull(perspectiveName);
    return _entries.TryRemove((perspectiveName, streamId), out _);
  }

  /// <summary>Everything currently remembered, for the health endpoint.</summary>
  /// <returns>The entries, in no particular order.</returns>
  public IReadOnlyList<Entry> Snapshot() {
    _forgetStale();
    return [.. _entries.Values];
  }

  private void _forgetStale() {
    var cutoff = _time.GetUtcNow() - ForgetAfter;
    foreach (var (key, entry) in _entries) {
      if (entry.LastFailedAt < cutoff) {
        _entries.TryRemove(new KeyValuePair<(string, Guid), Entry>(key, entry));
      }
    }
  }
}
