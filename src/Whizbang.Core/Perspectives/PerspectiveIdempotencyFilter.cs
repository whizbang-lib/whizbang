// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Decides whether an event has already been folded into a perspective row, from the row's recorded
/// position and the event's own. The generated perspective runner asks this once per event before Apply.
/// </summary>
/// <remarks>
/// <para>
/// The row records where it got to in two forms: the id of the last event applied to it, and that event's
/// <c>commit_sequence</c>. Only the second is a dependable position. A UUIDv7 id orders by time, but two
/// events committed within the same tick can be emitted in an order that their ids do not reflect, so an
/// id comparison can invert; <c>commit_sequence</c> is stamped after commit and cannot. The filter
/// therefore compares sequences whenever both sides have one.
/// </para>
/// <para>
/// When they do not, the question is not "which came first" but "can this row's position be compared at
/// all". It cannot: an id orders by when it was minted and by whom, which is not the order a stream that
/// several services feed stores them in. In every case where ordering cannot be established, the filter
/// defers: it reports not-applied and leaves an inversion to the rewind the worker performs for one.
/// The one case it still answers from ids is equality, which infers no ordering — an id equal to the
/// row's is that row's own event. That direction is deliberate. Re-applying an event a second time
/// is visible and recoverable; discarding one that was never applied leaves a read model permanently
/// wrong with nothing pending to repair it.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/apply-exactly-once#idempotency-filter</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveIdempotencyFilterTests.cs</tests>
public static class PerspectiveIdempotencyFilter {
  /// <summary>
  /// Reports whether <paramref name="incomingEventId"/> is already reflected in a row whose last applied
  /// event is described by <paramref name="lastAppliedEventId"/> and
  /// <paramref name="lastAppliedCommitSequence"/>.
  /// </summary>
  /// <param name="lastAppliedEventId">
  /// The row's recorded last applied event id, as stored in its metadata. Null or empty means the row
  /// records no position.
  /// </param>
  /// <param name="lastAppliedCommitSequence">That event's commit sequence, or null if it was never stamped.</param>
  /// <param name="incomingEventId">The id of the event being considered.</param>
  /// <param name="incomingCommitSequence">
  /// The incoming event's commit sequence, or null if the stamper has not reached it yet.
  /// </param>
  /// <returns><c>true</c> only when the event can be shown to be already applied.</returns>
  public static bool IsAlreadyApplied(
      string? lastAppliedEventId,
      long? lastAppliedCommitSequence,
      Guid incomingEventId,
      long? incomingCommitSequence) {
    if (string.IsNullOrEmpty(lastAppliedEventId)) {
      return false;
    }

    if (lastAppliedCommitSequence.HasValue && incomingCommitSequence.HasValue) {
      return incomingCommitSequence.Value <= lastAppliedCommitSequence.Value;
    }

    if (lastAppliedCommitSequence.HasValue || incomingCommitSequence.HasValue) {
      return false;
    }

    // Neither side is stamped, so there is no position either side can be read from.
    //
    // This used to compare the two ids as text, on the reasoning that a UUIDv7 encodes its timestamp in
    // the leading bytes, so lexical order is commit order. That holds only while ONE writer mints every
    // id in the order the stream stores them. A stream fed by more than one service breaks it: a v7 id
    // orders by the moment it was minted, on the service that minted it, which is not where it lands in
    // another service's stream. An event minted last by the service that owns it can be stored first
    // downstream, and then its id sorts above every event that follows it in that stream.
    //
    // Measured on a consuming deployment: one such inversion in a batch of twenty-four discarded the
    // whole batch, because every later event compared against that row's id and read as already applied.
    // Nothing was out of order in transit -- the stream's versions were contiguous and its commit
    // sequences monotonic. Only the ids were inverted, and they say nothing about this stream's order.
    //
    // So the ORDERING comparison is gone rather than narrowed again. What remains is equality, which
    // infers no order at all: an id equal to the row's recorded one is that row's own event, applied by
    // definition, and that is what makes a re-read of the same event a no-op.
    //
    // An id that merely sorts LOWER is an inversion, which PerspectiveWorker already answers by
    // rewinding to it. It is not evidence that the event was applied, and reporting not-applied is what
    // leaves it to that path. Re-applying an event is visible and recoverable; discarding one that was
    // never applied leaves the read model permanently wrong with nothing pending to repair it, which is
    // the asymmetry this filter exists to respect.
    return Guid.TryParse(lastAppliedEventId, out var lastApplied) && lastApplied == incomingEventId;
  }
}
