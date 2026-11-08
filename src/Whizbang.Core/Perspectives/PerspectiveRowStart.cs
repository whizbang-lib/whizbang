// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The business time a perspective row is dated at when a write inserts it: the first event folded into it, or its
/// stream's recorded start once the stream's first event is gone (#2152, #2154).
/// </summary>
/// <remarks>
/// <para>
/// A row's <c>created_at</c> is business time: when the entity came into being. The runner folds a batch and writes the
/// row once, and the write's own time is the last folded event's, which is <c>updated_at</c>. A row inserted by a write
/// that folded several events is dated at the first of them, so the value is the same however the events were batched,
/// live or in a rebuild (#2154). The superseding reaper and a prefix truncate delete a stream's first event outright; the
/// event store records the stream's start when it removes it, and a row whose fold no longer begins its stream is dated
/// at that start (#2152).
/// </para>
/// <para>
/// The generated runner owns both steps, so no perspective or store author has to remember them: before it writes a row
/// it resolves the date and begins it for that row, and the store's insert reads it through <see cref="For"/>. Ambient
/// rather than a store parameter for the reason <see cref="PerspectiveTableRedirect"/> is: the stores a runner writes
/// through keep their signatures, and a flow that never began a date is untouched. A date names its row, so a flow that
/// writes several rows never dates one at another's. An update never moves <c>created_at</c>, so only an insert reads it.
/// </para>
/// <para>
/// The cost is bounded to where it can matter. Nothing is resolved for a row known to exist. The event store is asked
/// only for a row that may be inserted whose first folded event is not its stream's first event, and it answers null
/// while the stream still holds its first event (the perspective simply folds none of the events before).
/// </para>
/// </remarks>
/// <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RebuildCreatedAtIntegrationTests.cs</tests>
public static class PerspectiveRowStart {
  private static readonly AsyncLocal<Start?> _current = new();
  private static readonly IDisposable _unchanged = new Handle(null, restores: false);

  /// <summary>The date this flow gives <paramref name="rowId"/> when it inserts the row, or null.</summary>
  /// <param name="rowId">The row being written.</param>
  /// <returns>The row's start, or null when this flow carries none for it.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs:Begin_DatesThatRowOnly_UntilTheHandleIsDisposedAsync</tests>
  public static DateTime? For(Guid rowId) =>
    _current.Value is { } start && start.RowId == rowId ? start.StartedAt : null;

  /// <summary>
  /// Dates <paramref name="rowId"/> at <paramref name="startedAt"/> in this flow until the handle is disposed. A null
  /// start changes nothing. Synchronous on purpose: a value set inside an async method does not flow back to its caller,
  /// so the start is begun by the caller that then awaits the write.
  /// </summary>
  /// <param name="rowId">The row being written.</param>
  /// <param name="startedAt">The row's date, or null to change nothing.</param>
  /// <returns>A handle that restores the start this flow carried before.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs:Begin_Nested_RestoresTheOuterStartOnDisposeAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs:Begin_WithNoStart_ChangesNothingAsync</tests>
  public static IDisposable Begin(Guid rowId, DateTime? startedAt) {
    if (startedAt is not { } at) {
      return _unchanged;
    }
    var outer = _current.Value;
    _current.Value = new Start(rowId, at);
    return new Handle(outer, restores: true);
  }

  /// <summary>
  /// The business time a row this write may insert is dated at: the stream's recorded start when the first folded event
  /// is not its stream's first event and the event store has one (the first event was removed), otherwise the first
  /// folded event's business time. Null for a row known to exist (an update keeps its created_at), for a fold with no
  /// event of the row's own stream, and for a first event with no hop, which carries no business time. Collectives are
  /// skipped: they are other streams' events folded into the row.
  /// </summary>
  /// <param name="eventStore">The store that knows the stream's start.</param>
  /// <param name="rowId">The row, which is its stream.</param>
  /// <param name="rowExists">Whether the row is known to exist; an existing row is updated and keeps its created_at.</param>
  /// <param name="folded">The events folded into the row, in order.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The row's date in UTC, or null to leave the write's own time.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs:ResolveAsync_AFoldStartingAfterTheFirstEvent_TakesTheStoresStartAsUtcAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs:ResolveAsync_AFoldThatBeginsTheStream_IsTheFirstEventsTime_NotTheLastsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs:ResolveAsync_AStreamThatStillHoldsItsStart_IsTheFirstFoldedEventsTimeAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs:ResolveAsync_ARowThatExists_AsksNothingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RebuildCreatedAtIntegrationTests.cs:LiveFirstBatch_OfSeveralEvents_DatesTheRowAtTheFirstAsync</tests>
  public static async Task<DateTime?> ResolveAsync(
      IEventStore eventStore, Guid rowId, bool rowExists, IReadOnlyList<MessageEnvelope<IEvent>> folded,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventStore);
    ArgumentNullException.ThrowIfNull(folded);
    var first = rowExists ? null : folded.FirstOrDefault(e => e.Payload is not ICollectiveEvent);
    if (first is null) {
      return null;
    }
    if (!first.IsFirstInLocalStream
        && await eventStore.GetStreamStartedAtAsync(rowId, cancellationToken).ConfigureAwait(false) is { } startedAt) {
      return DateTime.SpecifyKind(startedAt, DateTimeKind.Utc);
    }
    // The applied event's business time, read as the runner reads it for the write's own time: its last hop.
    return first.Hops is { Count: > 0 } hops ? hops[^1].Timestamp.UtcDateTime : null;
  }

  private sealed record Start(Guid RowId, DateTime StartedAt);

  private sealed class Handle(Start? outer, bool restores) : IDisposable {
    public void Dispose() {
      if (restores) {
        _current.Value = outer;
      }
    }
  }
}
