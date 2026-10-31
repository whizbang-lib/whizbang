// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// The inline half of <see cref="AppendToArchivedPolicy.AutoRestore"/> (#1738): a write its caller awaits that the store
/// refused because its stream is archived, under a type that restores on append, restores the stream through the archive
/// store and runs once more. Shared by the event store's direct append and the immediate and scoped strategies' flush, so
/// both retry the same way.
/// </summary>
/// <remarks>
/// Retried once, never in a loop: a second refusal (the archive was deleted, or a restore found a version conflict)
/// reaches the caller as it is. A refusal under <see cref="AppendToArchivedPolicy.Refuse"/>, a refusal that does not name
/// its stream, and a host with no archive store are not retried.
/// </remarks>
/// <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/ArchiveAwareEventStoreDecoratorTests.cs:AnAppend_AutoRestore_RestoresTheStream_AndIsRetriedOnceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:FlushAsync_AnAutoRestoreRefusal_RestoresTheStream_AndStoresTheWriteOnceMoreAsync</tests>
internal static class ArchivedStreamAutoRestore {
  /// <summary>Runs <paramref name="write"/>, restoring its stream and running it once more on an AutoRestore refusal.</summary>
  /// <param name="write">The write.</param>
  /// <param name="archive">Resolves the archive store, only when a refusal needs it; null when the host has none.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes when the write does.</returns>
  internal static async Task RunAsync(Func<Task> write, Func<IArchiveStore?> archive, CancellationToken cancellationToken) {
    try {
      await write().ConfigureAwait(false);
    } catch (StreamArchivedException refusal)
        when (refusal.Policy == AppendToArchivedPolicy.AutoRestore && refusal.StreamId != Guid.Empty && archive() is { } store) {
      _ = await store.RestoreAsync(refusal.StreamId, cancellationToken).ConfigureAwait(false);
      await write().ConfigureAwait(false);
    }
  }
}
