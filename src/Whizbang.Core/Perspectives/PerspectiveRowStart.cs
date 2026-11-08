// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The business time a perspective row began, for a row written without the event that began its stream (#2152).
/// RED stub: the surface the tests compile against, with no behavior yet.
/// </summary>
/// <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
public static class PerspectiveRowStart {
  /// <summary>The start this flow dates <paramref name="rowId"/> at when it inserts the row, or null.</summary>
  /// <param name="rowId">The row being written.</param>
  /// <returns>Null: not implemented yet.</returns>
  public static DateTime? For(Guid rowId) {
    _ = rowId;
    return null;
  }

  /// <summary>Dates <paramref name="rowId"/> at <paramref name="startedAt"/> in this flow until the handle is disposed.</summary>
  /// <param name="rowId">The row being written.</param>
  /// <param name="startedAt">The stream's start, or null to change nothing.</param>
  /// <returns>A handle that does nothing yet.</returns>
  public static IDisposable Begin(Guid rowId, DateTime? startedAt) {
    _ = rowId;
    _ = startedAt;
    return new Nothing();
  }

  /// <summary>The stream's start, when the row may be inserted and its fold does not begin with the stream's first event.</summary>
  /// <param name="eventStore">The store that knows the stream's start.</param>
  /// <param name="rowId">The row, which is its stream.</param>
  /// <param name="rowExists">Whether the row is known to exist.</param>
  /// <param name="folded">The events folded into the row, in order.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Null: not implemented yet.</returns>
  public static Task<DateTime?> ResolveAsync(
      IEventStore eventStore, Guid rowId, bool rowExists, IReadOnlyList<MessageEnvelope<IEvent>> folded,
      CancellationToken cancellationToken = default) {
    _ = (eventStore, rowId, rowExists, folded, cancellationToken);
    return Task.FromResult<DateTime?>(null);
  }

  private sealed class Nothing : IDisposable {
    public void Dispose() { }
  }
}
