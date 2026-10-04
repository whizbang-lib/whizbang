using Whizbang.Core.ValueObjects;

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
/// all". A row written by an older version of this library has no sequence and an id that is not a UUIDv7,
/// so comparing it as text against a UUIDv7 is meaningless rather than merely imprecise. In every case
/// where ordering cannot be established, the filter defers: it reports not-applied and lets Apply's own
/// idempotency handle a true duplicate. That direction is deliberate. Re-applying an event a second time
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

    // Neither side is stamped. Comparing ids as text is only meaningful when both ids are time-ordered:
    // a UUIDv7 encodes its timestamp in the leading bytes, so lexical order is commit order. A v4 id
    // encodes nothing there. Its leading nibble is random, so it sits at an arbitrary point in the same
    // ordering -- about 15 times in 16 above every UUIDv7 generated this decade -- and comparing against
    // it does not produce a worse answer, it produces an unrelated one.
    //
    // It is weaker than that even. Where rows carrying such metadata have been examined, the stored id
    // matched no event in the store at all: it is a value an older version wrote into the row, not the
    // id of the event that was applied to it. So the row records no position this filter can read, and
    // the only safe reading of it is that nothing is known -- not that the event came earlier.
    if (!Guid.TryParse(lastAppliedEventId, out var lastApplied)
        || !TrackedGuid.FromExternal(lastApplied).IsTimeOrdered
        || !TrackedGuid.FromExternal(incomingEventId).IsTimeOrdered) {
      return false;
    }

    // Both canonical, so the comparison cannot turn on how the stored id happened to be cased.
    return string.Compare(
        incomingEventId.ToString("D"), lastApplied.ToString("D"), StringComparison.Ordinal) <= 0;
  }
}
