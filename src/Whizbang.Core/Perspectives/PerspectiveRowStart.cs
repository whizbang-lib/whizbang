// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The business time a perspective row began, for a row written without the event that began its stream (#2152).
/// </summary>
/// <remarks>
/// <para>
/// A row's <c>created_at</c> is business time, taken from the events a write folds. The superseding reaper and a prefix
/// truncate delete a stream's first event outright, and a rebuild that then inserts the row (blue-green into an empty
/// shadow table, or in place where the row is missing) had only the events that survived to date it by. The event store
/// records the stream's start when it removes the first event, and <see cref="ResolveAsync"/> reads it back.
/// </para>
/// <para>
/// The generated runner owns both steps, so no perspective or store author has to remember them: before it writes a row
/// it resolves the start and begins it for that row, and the store's insert reads it through <see cref="For"/>. Ambient
/// rather than a store parameter for the reason <see cref="PerspectiveTableRedirect"/> is: the stores a runner writes
/// through keep their signatures, and a flow that never began a start is untouched. A start names its row, so a flow
/// that writes several rows never dates one at another's stream. An update never moves <c>created_at</c>, so only an
/// insert reads it.
/// </para>
/// <para>
/// The cost is bounded to where it can matter. The store is asked only for a row that is not known to exist, whose first
/// folded event is not its stream's first event; the answer is null while the stream still holds its first event, so a
/// stream with its whole history is dated exactly as before.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RebuildCreatedAtIntegrationTests.cs</tests>
public static class PerspectiveRowStart {
  private static readonly AsyncLocal<Start?> _current = new();
  private static readonly IDisposable _unchanged = new Handle(null, restores: false);

  /// <summary>The start this flow dates <paramref name="rowId"/> at when it inserts the row, or null.</summary>
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
  /// <param name="startedAt">The stream's start, or null to change nothing.</param>
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
  /// The stream's start, when the row may be inserted and its fold does not begin with the stream's first event: asked of
  /// <paramref name="eventStore"/>, which answers null while the stream still holds its first event. Null without a
  /// question otherwise. Collectives are skipped: they are other streams' events folded into the row.
  /// </summary>
  /// <param name="eventStore">The store that knows the stream's start.</param>
  /// <param name="rowId">The row, which is its stream.</param>
  /// <param name="rowExists">Whether the row is known to exist; an existing row is updated and keeps its created_at.</param>
  /// <param name="folded">The events folded into the row, in order.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The stream's start in UTC, or null.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs:ResolveAsync_AFoldStartingAfterTheFirstEvent_TakesTheStoresStartAsUtcAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs:ResolveAsync_AFoldThatBeginsTheStream_AsksNothingAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowStartTests.cs:ResolveAsync_ARowThatExists_AsksNothingAsync</tests>
  public static async Task<DateTime?> ResolveAsync(
      IEventStore eventStore, Guid rowId, bool rowExists, IReadOnlyList<MessageEnvelope<IEvent>> folded,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventStore);
    ArgumentNullException.ThrowIfNull(folded);
    if (rowExists || !_startsAfterTheStreamsFirstEvent(folded)) {
      return null;
    }
    var startedAt = await eventStore.GetStreamStartedAtAsync(rowId, cancellationToken).ConfigureAwait(false);
    return startedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null;
  }

  // Whether the first stream event folded is not its stream's first event. A fold of collectives alone has none.
  private static bool _startsAfterTheStreamsFirstEvent(IReadOnlyList<MessageEnvelope<IEvent>> folded) {
    var first = folded.FirstOrDefault(e => e.Payload is not ICollectiveEvent);
    return first?.IsFirstInLocalStream == false;
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
