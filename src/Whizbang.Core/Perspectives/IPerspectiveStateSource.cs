// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Where a set-based batch reads the rows it folds onto: the state-source seam of the set-based write mode.
/// </summary>
/// <remarks>
/// <para>
/// The per-pair path reads each pair's row with its own round trips: the row version and metadata, then the model,
/// and for a missing row the purge-marker probe and, for a perspective whose rows can be evicted, the history probe.
/// A batch of N pairs cost N times that. A state source answers the same questions for the whole batch: one read of
/// the rows per perspective table (<c>id = ANY</c>), and one query each for the purge markers and the history of the
/// streams whose rows are missing.
/// </para>
/// <para>
/// A stream the batch knows is new is not read at all: its cursor is absent and the first event the batch folds is the
/// first event of its stream (<see cref="Observability.MessageEnvelope{T}.IsFirstInLocalStream"/>, #1878), so no row,
/// purge marker or earlier history can exist for it. A row that exists anyway (written by something other than its
/// stream's events) costs nothing wrong: the batch inserts the row only where it is absent, and a row it finds present is
/// a conflict that reruns the pair on the per-pair path.
/// </para>
/// <para>
/// Whatever a batch cannot answer (the store does not read rows in batches, a batched read failed, a question it was
/// not asked to plan for) the runner reads per stream, exactly as without a state source. Only a prepare consults it:
/// a pair rerun on the per-pair path always reads its row afresh.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#state-source</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/BatchedPerspectiveStateSourceTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectivePrepareCompleteIntegrationTests.StateSource.cs</tests>
public interface IPerspectiveStateSource {
  /// <summary>Plans the reads of one batch.</summary>
  /// <param name="pairs">The pairs the batch prepares.</param>
  /// <returns>The batch's reads, handed to every <see cref="IPerspectiveRunner.PrepareAsync(Guid, string, Guid?, IReadOnlyList{Observability.MessageEnvelope{IEvent}}, IPerspectiveBatchState?, CancellationToken)"/> of the batch.</returns>
  IPerspectiveBatchState BeginBatch(IReadOnlyList<PerspectiveStateRequest> pairs);
}

/// <summary>One pair a batch prepares, as its state source plans for it.</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="PerspectiveName">The perspective.</param>
/// <param name="FirstEventId">The first event the batch folds for the pair, which the history probe asks before.</param>
/// <param name="KnownNew">
/// Whether the stream is known to be new: its cursor is absent and <paramref name="FirstEventId"/> is the first event
/// of its stream.
/// </param>
/// <param name="EventIds">
/// The events the batch folds for the pair, of which the last one applied is the one whose commit sequence the row's
/// write records; null when the batch does not plan that read.
/// </param>
/// <docs>fundamentals/perspectives/write-modes#state-source</docs>
public readonly record struct PerspectiveStateRequest(
    Guid StreamId, string PerspectiveName, Guid FirstEventId, bool KnownNew, IReadOnlyList<Guid>? EventIds = null) {
  /// <summary>
  /// Whether a batch whose cursor is <paramref name="cursor"/> and whose events are <paramref name="events"/> starts a
  /// new stream: no cursor, and the first event is the first event of its stream.
  /// </summary>
  /// <param name="cursor">The pair's cursor (the last event it processed), or null when it has none.</param>
  /// <param name="events">The batch's events, in the order they are applied.</param>
  /// <returns>True when no row, purge marker or earlier history can exist for the stream.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/BatchedPerspectiveStateSourceTests.cs:IsKnownNew_OnlyWithoutACursor_AndWhenTheFirstEventStartsTheStreamAsync</tests>
  public static bool IsKnownNew(Guid? cursor, IReadOnlyList<Observability.MessageEnvelope<IEvent>> events) {
    ArgumentNullException.ThrowIfNull(events);
    return cursor is null && events.Count > 0 && events[0].IsFirstInLocalStream;
  }
}

/// <summary>
/// The reads of one batch, planned by an <see cref="IPerspectiveStateSource"/>. Each answer is null when the batch does
/// not have it, and the runner then reads per stream.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#state-source</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/BatchedPerspectiveStateSourceTests.cs</tests>
public interface IPerspectiveBatchState {
  /// <summary>Whether the batch knows the pair's stream is new, so its row, purge marker and history need no read.</summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="perspectiveName">The perspective.</param>
  /// <returns>True for a stream the batch planned as known new.</returns>
  bool IsKnownNew(Guid streamId, string perspectiveName);

  /// <summary>
  /// The pair's row as the batch read it, with every other row of the perspective's batch in one read, or null when
  /// the batch cannot answer.
  /// </summary>
  /// <typeparam name="TModel">The perspective's model.</typeparam>
  /// <param name="store">The perspective's store, which reads the batch's rows when no other pair has yet.</param>
  /// <param name="streamId">The stream.</param>
  /// <param name="perspectiveName">The perspective.</param>
  /// <param name="cancellationToken">Cancels the read.</param>
  /// <returns>The row (its model null when absent), or null to read it per stream.</returns>
  Task<PerspectiveStoredRow<TModel>?> ReadRowAsync<TModel>(
      IPerspectiveStore<TModel> store, Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      where TModel : class;

  /// <summary>
  /// Whether a pair whose row is missing is purged, answered with every other missing row of the perspective's batch
  /// in one query, or null when the batch cannot answer.
  /// </summary>
  /// <param name="markers">The purge markers.</param>
  /// <param name="streamId">The stream.</param>
  /// <param name="perspectiveName">The perspective.</param>
  /// <param name="cancellationToken">Cancels the query.</param>
  /// <returns>The answer, or null to ask per stream.</returns>
  Task<bool?> IsPurgedAsync(
      IPerspectivePurgeMarkerStore markers, Guid streamId, string perspectiveName, CancellationToken cancellationToken = default);

  /// <summary>
  /// Whether a pair whose row is missing has history of <paramref name="eventTypes"/> before
  /// <paramref name="beforeEventId"/>, answered with every other missing row of the perspective's batch in one query, or
  /// null when the batch cannot answer.
  /// </summary>
  /// <param name="eventStore">The event store.</param>
  /// <param name="streamId">The stream.</param>
  /// <param name="perspectiveName">The perspective.</param>
  /// <param name="beforeEventId">The event the history is asked before.</param>
  /// <param name="eventTypes">The event types the perspective folds.</param>
  /// <param name="cancellationToken">Cancels the query.</param>
  /// <returns>The answer, or null to ask per stream.</returns>
  Task<bool?> HasHistoryBeforeAsync(
      IEventStore eventStore, Guid streamId, string perspectiveName, Guid beforeEventId, IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// The commit sequence of an event the batch planned for one of its pairs, answered for every planned event of the
  /// batch with one query, or null when the batch cannot answer.
  /// </summary>
  /// <param name="eventStore">The event store.</param>
  /// <param name="eventId">The event.</param>
  /// <param name="cancellationToken">Cancels the query.</param>
  /// <returns>The answer (its value null when the event is not yet stamped), or null to ask per event.</returns>
  Task<PerspectiveCommitSequence?> CommitSequenceOfAsync(
      IEventStore eventStore, Guid eventId, CancellationToken cancellationToken = default);
}

/// <summary>An event's commit sequence as a batch read it: null when the event is not stamped (or not stored).</summary>
/// <param name="Value">The commit sequence, or null.</param>
/// <docs>fundamentals/perspectives/write-modes#state-source</docs>
public readonly record struct PerspectiveCommitSequence(long? Value);

/// <summary>A perspective row as a batched read found it: the apply read and the model (null when the row is absent).</summary>
/// <typeparam name="TModel">The perspective's model.</typeparam>
/// <param name="ApplyRead">The row's version, metadata and scope, as <see cref="IPerspectiveStore{TModel}.ReadForApplyAsync"/> reads them.</param>
/// <param name="Model">The model, as <see cref="IPerspectiveStore{TModel}.GetByStreamIdAsync"/> reads it, or null when the row is absent.</param>
/// <docs>fundamentals/perspectives/write-modes#state-source</docs>
public sealed record PerspectiveStoredRow<TModel>(PerspectiveApplyRead ApplyRead, TModel? Model) where TModel : class;
