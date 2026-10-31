// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// What an append to a stream archived whole does, declared per stream type
/// (<c>Whizbang:StreamLifecycle:Types:&lt;StreamType&gt;:AppendToArchived</c>). A stream archived whole keeps a tombstone
/// that holds its versions, and an append would number from nothing hot and reuse one, so every write path applies the
/// policy before it stores.
/// </summary>
/// <remarks>
/// A third policy, starting a new generation of the stream from its final snapshot, needs the final snapshot that
/// finalization takes (#1734) and ships with it; it is not a value here yet, so a configuration that names it is refused
/// at startup rather than read as one of these.
/// </remarks>
/// <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamLifecycleOptionsBinderTests.cs</tests>
public enum AppendToArchivedPolicy {
  /// <summary>
  /// The default. A write its caller awaits throws <see cref="StreamArchivedException"/>; a write nobody awaits is
  /// dead-lettered alone with <see cref="Messaging.MessageFailureReason.StreamArchived"/> and held for review, and the
  /// rest of its batch is stored.
  /// </summary>
  Refuse = 0,

  /// <summary>
  /// A write its caller awaits restores the stream inline (<see cref="IArchiveStore.RestoreAsync"/>) and is retried once.
  /// A write nobody awaits is dead-lettered with <see cref="Messaging.MessageFailureReason.AwaitingRestore"/>, the
  /// stream's restore is queued in the same transaction, and once a maintenance step has restored the stream the dead
  /// letter is redriven by the dead-letter recovery worker.
  /// </summary>
  AutoRestore = 1,
}
