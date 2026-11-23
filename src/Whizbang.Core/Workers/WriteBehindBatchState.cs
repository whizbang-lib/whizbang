// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Workers;

/// <summary>
/// The state a write-behind batch (#2247) prepares with: a pair whose model is held folds onto the held model, not the
/// database row (which does not have it yet), and every other question goes to the state source's batch.
/// </summary>
/// <remarks>
/// A held model is never known to be new, whatever its stream's first event: the held writes created it. A pair of a
/// write-behind perspective keeps its model, so the batch can hold it in turn.
/// </remarks>
/// <param name="inner">The state source's batch, or null when there is none and every row is read per stream.</param>
/// <param name="heldRow">The held model of a (stream, perspective), or null.</param>
/// <param name="writesBehind">Whether a perspective writes behind.</param>
/// <param name="onLookup">Told, for each row a write-behind pair reads, whether a held model answered it.</param>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/WriteBehindBatchStateTests.cs</tests>
internal sealed class WriteBehindBatchState(
    IPerspectiveBatchState? inner,
    Func<Guid, string, PerspectiveWriteBehindRow?> heldRow,
    Func<string, bool> writesBehind,
    Action<string, bool> onLookup) : IPerspectiveBatchState {
  /// <inheritdoc/>
  public bool KeepsModels(Guid streamId, string perspectiveName) => writesBehind(perspectiveName);

  /// <inheritdoc/>
  public bool IsKnownNew(Guid streamId, string perspectiveName) =>
    heldRow(streamId, perspectiveName) is null && inner?.IsKnownNew(streamId, perspectiveName) == true;

  /// <inheritdoc/>
  public async Task<PerspectiveStoredRow<TModel>?> ReadRowAsync<TModel>(
      IPerspectiveStore<TModel> store, Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      where TModel : class {
    var held = heldRow(streamId, perspectiveName);
    if (writesBehind(perspectiveName)) {
      onLookup(perspectiveName, held is not null);
    }
    if (held?.ReadAs<TModel>() is { } row) {
      return row;
    }
    return inner is null ? null : await inner.ReadRowAsync(store, streamId, perspectiveName, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc/>
  public Task<bool?> IsPurgedAsync(
      IPerspectivePurgeMarkerStore markers, Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
    inner?.IsPurgedAsync(markers, streamId, perspectiveName, cancellationToken) ?? Task.FromResult<bool?>(null);

  /// <inheritdoc/>
  public Task<bool?> HasHistoryBeforeAsync(
      IEventStore eventStore, Guid streamId, string perspectiveName, Guid beforeEventId, IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) =>
    inner?.HasHistoryBeforeAsync(eventStore, streamId, perspectiveName, beforeEventId, eventTypes, cancellationToken)
      ?? Task.FromResult<bool?>(null);

  /// <inheritdoc/>
  public Task<PerspectiveCommitSequence?> CommitSequenceOfAsync(
      IEventStore eventStore, Guid eventId, CancellationToken cancellationToken = default) =>
    inner?.CommitSequenceOfAsync(eventStore, eventId, cancellationToken) ?? Task.FromResult<PerspectiveCommitSequence?>(null);
}
