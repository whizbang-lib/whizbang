// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// What an append to a stream archived whole does, declared per stream type
/// (<c>Whizbang:StreamLifecycle:Types:&lt;StreamType&gt;:AppendToArchived</c>). A stream archived whole keeps a tombstone
/// that holds its versions, and an append would number from nothing hot and reuse one, so every write path applies the
/// policy before it stores.
/// </summary>
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

  /// <summary>
  /// The append starts a new generation of the stream (#1734): it is stored at the tombstone's last version plus one,
  /// the stream becomes active again with its archive as a prefix, and each perspective resumes after its parked cursor,
  /// from the final snapshot taken there. When a perspective holds no snapshot at its parked cursor, the append is treated
  /// as <see cref="AutoRestore"/>.
  /// </summary>
  NewGeneration = 2,
}
