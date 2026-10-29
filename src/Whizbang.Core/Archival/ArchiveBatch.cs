// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// One unit of work for <see cref="IArchiveStore.WriteAsync"/>: one stream, and either the prefix through a version
/// or the whole stream.
/// </summary>
/// <remarks>
/// A batch never spans streams, because a stream is the unit the archive must keep atomic (copy then truncate, all or
/// nothing) and the unit a tombstone describes. A sweep that archives many streams sends one batch per stream, so one
/// stream's refusal (unconsumed work, an active hold) never holds back another's. How a store splits a large batch
/// into chunks is the store's own concern: the Postgres-table store copies it in one statement; a blob store writes
/// as many chunks as it needs before it truncates.
/// </remarks>
/// <docs>fundamentals/events/archive-store#batches</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/ArchiveBatchTests.cs</tests>
public sealed record ArchiveBatch {
  private ArchiveBatch(Guid streamId, long? throughVersion) {
    StreamId = streamId;
    ThroughVersion = throughVersion;
  }

  /// <summary>The stream to archive.</summary>
  public Guid StreamId { get; }

  /// <summary>
  /// The last version archived (inclusive) for a prefix batch; <see langword="null"/> for a whole-stream batch.
  /// </summary>
  public long? ThroughVersion { get; }

  /// <summary>True when the batch archives every event of the stream and leaves a tombstone.</summary>
  public bool IsWholeStream => ThroughVersion is null;

  /// <summary>
  /// The events at or below <paramref name="throughVersion"/>. The prefix truncate's guards apply unchanged: an event
  /// must survive above the version (the carry-forward), so a prefix batch never empties a stream.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="throughVersion">The last version archived. Zero or more: a directly appended stream starts at 0.</param>
  /// <returns>The batch.</returns>
  /// <exception cref="ArgumentException"><paramref name="streamId"/> is the empty id.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="throughVersion"/> is negative.</exception>
  public static ArchiveBatch Prefix(Guid streamId, long throughVersion) {
    ArgumentOutOfRangeException.ThrowIfNegative(throughVersion);
    return new ArchiveBatch(_notEmpty(streamId), throughVersion);
  }

  /// <summary>
  /// Every event of the stream. The stream keeps a tombstone (state <c>Archived</c>, its last version and the
  /// locator), and its versions are never reused.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <returns>The batch.</returns>
  /// <exception cref="ArgumentException"><paramref name="streamId"/> is the empty id.</exception>
  public static ArchiveBatch WholeStream(Guid streamId) => new(_notEmpty(streamId), throughVersion: null);

  // The empty id is the store's "no stream" row (commands, unstreamed events); archiving it would sweep unrelated rows.
  private static Guid _notEmpty(Guid streamId) =>
    streamId == Guid.Empty ? throw new ArgumentException("The empty stream id names no stream.", nameof(streamId)) : streamId;
}
