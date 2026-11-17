// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// What the last completed rewind of each stream and perspective started from (its trigger and the stream's last
/// version), and what it ended in, so a rewind asked for again with the same input is settled without running (#2366).
/// </summary>
/// <remarks>
/// <para>
/// A rewind replays the stream's events into the perspective. Asked again for the same trigger with no event stored on
/// the stream since, it reads the same events in the same order and can only reach the state the last one reached.
/// Running it again costs a full replay and writes a rewind-started and rewind-completed pair, and nothing changes; a
/// stream something keeps flagging would do that forever. The repeat is settled with the last rewind's completion
/// instead: its work rows complete and its flag clears exactly as after the rewind itself.
/// </para>
/// <para>
/// Bounded: past <see cref="Capacity"/> streams remembered, the memory is cleared and refilled, which costs at most one
/// more rewind per stream.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rewind#a-repeated-rewind-is-settled</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/RewindRepeatGuardTests.cs</tests>
internal sealed class RewindRepeatGuard(int capacity = RewindRepeatGuard.DEFAULT_CAPACITY) {
  /// <summary>How many streams are remembered at most, unless told otherwise.</summary>
  internal const int DEFAULT_CAPACITY = 10_000;

  private readonly ConcurrentDictionary<(Guid StreamId, string PerspectiveName), Outcome> _last = new();

  /// <summary>How many streams are remembered at most.</summary>
  internal int Capacity { get; } = capacity;

  /// <summary>A completed rewind: the input it ran on, what it ended in, and how often it has been asked for again.</summary>
  internal sealed record Outcome(Guid TriggerEventId, long StreamVersion, PerspectiveCursorCompletion Completion, int Repeats);

  /// <summary>
  /// The last rewind of the stream when it ran on the same trigger and the same last version, with this request counted
  /// as one more repeat; otherwise null, and the rewind runs.
  /// </summary>
  internal Outcome? Repeat(Guid streamId, string perspectiveName, Guid triggerEventId, long streamVersion) {
    var key = (streamId, perspectiveName);
    if (!_last.TryGetValue(key, out var last) || last.TriggerEventId != triggerEventId || last.StreamVersion != streamVersion) {
      return null;
    }
    var repeated = last with { Repeats = last.Repeats + 1 };
    _last[key] = repeated;
    return repeated;
  }

  /// <summary>Remembers a completed rewind, replacing whatever was remembered for the stream.</summary>
  internal void Remember(Guid streamId, string perspectiveName, Guid triggerEventId, long streamVersion,
      PerspectiveCursorCompletion completion) {
    var key = (streamId, perspectiveName);
    if (_last.Count >= Capacity && !_last.ContainsKey(key)) {
      _last.Clear();
    }
    _last[key] = new Outcome(triggerEventId, streamVersion, completion, Repeats: 0);
  }
}
