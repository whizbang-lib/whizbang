// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Records which streams a perspective has purged, so a purged row stays purged.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ModelAction.Purge"/> removes a perspective row and leaves nothing behind. Without a record of the
/// purge, the next event on the stream finds no row, is applied to an empty model, and a create-or-update Apply
/// builds a mostly default row that then shows in grids and counts. The generated runner writes a marker when an
/// Apply purges and consults it when the row is missing: a marked stream skips its later events (logged, and
/// counted in <c>whizbang.perspective.purged_events_skipped</c>) unless an Apply returns
/// <see cref="ModelAction.Resurrect"/>, which recreates the row and clears the marker.
/// </para>
/// <para>
/// The marker is consulted only when the row is missing, and a marker only exists after a purge, so a stream
/// that was never purged, new or live, is never affected and the steady state pays nothing. Live drain, rewind
/// and rebuild consult it the same way. An operator purge (<see cref="Whizbang.Core.Messaging.IStreamPurger"/>)
/// marks a stream for every perspective at once with <see cref="PerspectivePurgeMarkers.ALL_PERSPECTIVES"/>.
/// </para>
/// <para>
/// The runner writes the marker <em>before</em> it removes the row and clears it only <em>after</em> it writes
/// the resurrected row, so a crash between the two leaves a marker on a live row (harmless: a live row never
/// consults it) rather than a missing row without a marker.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PurgeStaysPurgedTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectivePurgeMarkerStoreTests.cs</tests>
public interface IPerspectivePurgeMarkerStore {
  /// <summary>
  /// Whether the stream is purged for the perspective: a marker for that perspective, or one for every
  /// perspective (<see cref="PerspectivePurgeMarkers.ALL_PERSPECTIVES"/>), exists.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="perspectiveName">The perspective, as the runner names it.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task<bool> IsPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default);

  /// <summary>Records that the perspective purged the stream. Idempotent: a second purge refreshes the marker.</summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="perspectiveName">The perspective, as the runner names it.</param>
  /// <param name="purgeEventId">The event whose Apply purged, when one did.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task MarkPurgedAsync(Guid streamId, string perspectiveName, Guid? purgeEventId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Forgets the perspective's purge of the stream after an Apply resurrected it. A marker for every
  /// perspective is left in place: it still governs the others, and this perspective's row now exists.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="perspectiveName">The perspective, as the runner names it.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task ClearAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default);
}

/// <summary>Names shared by every <see cref="IPerspectivePurgeMarkerStore"/>.</summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
public static class PerspectivePurgeMarkers {
  /// <summary>
  /// The perspective name of a marker that applies to every perspective: an operator purge removed the whole
  /// stream. No perspective is named <c>*</c>.
  /// </summary>
  public const string ALL_PERSPECTIVES = "*";
}
