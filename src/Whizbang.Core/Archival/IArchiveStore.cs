// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Archival;

/// <summary>
/// Where a stream's archived detail lives once it leaves the hot event store (WS-3 S7). The framework ships a
/// Postgres-table store (<c>wh_event_archive</c>, registered by both Postgres drivers); a blob store replaces it by
/// registering its own <see cref="IArchiveStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The contract every store keeps:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="WriteAsync"/> copies the batch's events to the store and only then removes them from
/// the hot store. Either both happen or neither does: a failure at any point leaves every event hot.</description></item>
/// <item><description>Every successful write bumps the origin generation, so stream integrity re-verifies instead of
/// alarming on the history that moved.</description></item>
/// <item><description>A whole-stream write leaves a tombstone (the stream's lifecycle row, state <c>Archived</c>, with
/// its last version and the <see cref="ArchiveLocator"/>), and no append may reuse a version at or below that last
/// version.</description></item>
/// <item><description><see cref="ReadAsync"/> returns each event exactly as it was stored (the same body, metadata
/// and scope text), in version order.</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/events/archive-store</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveStoreTests.cs</tests>
public interface IArchiveStore {
  /// <summary>
  /// Archives one batch: a stream's prefix through a version, or the whole stream. Copy then truncate, atomically.
  /// </summary>
  /// <param name="batch">The stream and how much of it.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The outcome: <see cref="ArchiveStatus.ARCHIVED"/>, or why nothing moved.</returns>
  /// <docs>fundamentals/events/archive-store#writing-an-archive</docs>
  Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default);

  /// <summary>
  /// Reads a stream's archived events at or above <paramref name="fromVersion"/>, in version order, byte-identical to
  /// what the hot store held.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="fromVersion">The first version to return (inclusive).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The archived events, streamed in version order.</returns>
  /// <docs>fundamentals/events/archive-store#reading-an-archive</docs>
  IAsyncEnumerable<ArchivedEvent> ReadAsync(Guid streamId, long fromVersion = 0, CancellationToken cancellationToken = default);

  /// <summary>
  /// Puts a stream's archived events back into the hot store at their original versions and positions, returns the
  /// stream to <c>Active</c>, and removes the archived copy. Idempotent: a second call finds nothing archived.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The outcome: <see cref="ArchiveStatus.RESTORED"/> or <see cref="ArchiveStatus.NOTHING_ARCHIVED"/>.</returns>
  /// <docs>fundamentals/events/archive-store#restoring-an-archive</docs>
  Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Deletes a stream's archived copy for good. The tombstone stays (without a locator), so the stream's versions are
  /// still never reused.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many archived events were deleted.</returns>
  /// <docs>fundamentals/events/archive-store#deleting-an-archive</docs>
  Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default);
}
