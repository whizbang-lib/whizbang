// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Unit tests for <see cref="PerspectiveIdempotencyFilter"/>: the per-event decision the generated
/// perspective runner makes before Apply (#1054). The rule the whole type exists to hold is that an event
/// which has never been applied is never discarded, so most of these assert deferral rather than ordering.
/// </summary>
/// <docs>fundamentals/perspectives/apply-exactly-once#idempotency-filter</docs>
/// <tests>Whizbang.Core/Perspectives/PerspectiveIdempotencyFilter.cs</tests>
public class PerspectiveIdempotencyFilterTests {
  // A v4 id is what an older version of this library left in a row's metadata. The leading nibble is
  // random, so ~15 in 16 such ids sort above any UUIDv7 generated this decade, and ~1 in 16 below.
  // Both are represented, because which side a legacy id falls on is the coin this defect turned on.
  private const string LEGACY_V4_EVENT_ID = "f4780968-1921-4abe-a191-8da21cfafdde";
  private const string LEGACY_V4_SORTING_BELOW = "00780968-1921-4abe-a191-8da21cfafdde";

  // A UUIDv7 whose timestamp bytes are zero: still version 7, but it sorts below the v4 above. Used to
  // force the lexical comparison to the WRONG answer, so only the version guard can produce the right
  // one. Without it a test reads as green because of how two ids happened to sort, not because the code
  // is correct.
  private const string V7_EARLIEST = "00000000-0000-7000-8000-000000000001";

  private static Guid _v7() => TrackedGuid.New().Value;

  // Two UUIDv7s in a known order, so a test can force the lexical comparison to the answer that would
  // be WRONG and prove the branch under test is what produced the right one.
  private const string V7_EARLIER = "01a00000-0000-7000-8000-000000000001";
  private const string V7_LATER = "01a0ffff-ffff-7fff-bfff-ffffffffffff";

  [Test]
  public async Task IsAlreadyApplied_BothCommitSequences_ComparesThemAsync() {
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(_v7().ToString("D"), 100, _v7(), 100))
      .IsTrue().Because("An event at the row's own sequence is the one the row already reflects.");
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(_v7().ToString("D"), 100, _v7(), 99))
      .IsTrue().Because("A lower sequence committed earlier, so the row already includes it.");
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(_v7().ToString("D"), 100, _v7(), 101))
      .IsFalse().Because("A higher sequence committed after the row's position and still has to be applied.");
  }

  [Test]
  public async Task IsAlreadyApplied_BothCommitSequences_IgnoresTheEventIdsAsync() {
    // The ids here are in the opposite order to the sequences. commit_sequence wins, because it is
    // stamped after commit and cannot invert the way two same-tick UUIDv7s can.
    var older = TrackedGuid.New().Value;
    var newer = TrackedGuid.New().Value;

    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(newer.ToString("D"), 100, older, 101))
      .IsFalse();
  }

  [Test]
  public async Task IsAlreadyApplied_OnlyTheRowHasASequence_DefersAsync() =>
    // The ids are arranged so that falling through to a lexical comparison would report TRUE. Only the
    // deferral can produce false here, so this fails if that branch is ever removed.
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(
        V7_LATER, 100, Guid.Parse(V7_EARLIER), null))
      .IsFalse().Because("The stamper has not reached the incoming event, so there is nothing to compare "
        + "it against; an id comparison here is the inversion the sequence exists to avoid.");

  [Test]
  public async Task IsAlreadyApplied_OnlyTheEventHasASequence_DefersAsync() =>
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(
        V7_LATER, null, Guid.Parse(V7_EARLIER), 100))
      .IsFalse().Because("The row's position predates stamping, so the two positions are not comparable.");

  [Test]
  public async Task IsAlreadyApplied_NoRecordedPosition_DefersAsync() {
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(null, null, _v7(), null)).IsFalse();
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied("", null, _v7(), null)).IsFalse()
      .Because("A row with no last applied event has nothing to have already applied.");
  }

  // ---------------------------------------------------------------------------------------------- #
  // Neither side stamped. This is the single-source case the id comparison was written for, and the
  // case that lost data on a consumer's cutover (#1054).
  // ---------------------------------------------------------------------------------------------- #

  [Test]
  public async Task IsAlreadyApplied_NeitherStamped_BothIdsTimeOrdered_ComparesThemAsync() {
    var first = TrackedGuid.New().Value;
    var second = TrackedGuid.New().Value;

    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(second.ToString("D"), null, first, null))
      .IsTrue().Because("Two UUIDv7s from this generator order by time, so an earlier id really was "
        + "applied already. Keeping this case working is why the comparison is narrowed rather than removed.");
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(first.ToString("D"), null, second, null))
      .IsFalse();
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(first.ToString("D"), null, first, null))
      .IsTrue().Because("The row's own event is applied by definition.");
  }

  [Test]
  public async Task IsAlreadyApplied_NeitherStamped_RowIdIsNotTimeOrdered_DefersAsync() =>
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(LEGACY_V4_EVENT_ID, null, _v7(), null))
      .IsFalse().Because("A v4 id has no ordering relationship to a UUIDv7, so comparing them as text is "
        + "meaningless, not merely imprecise. This is the case that discarded four confirmed writes: the "
        + "row sorted above every id generated this decade, so its own events read as already applied.");

  [Test]
  public async Task IsAlreadyApplied_NeitherStamped_RowIdSortsBelow_StillDefersAsync() =>
    // The ~1 in 16 legacy ids that sort BELOW a UUIDv7 still have to defer: the right answer cannot
    // depend on which random nibble a legacy id happened to get. The incoming id is chosen to sort below
    // the row's, so a comparison would report already-applied and only the version guard says otherwise.
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(
        LEGACY_V4_SORTING_BELOW, null, Guid.Parse(V7_EARLIEST), null)).IsFalse();

  [Test]
  public async Task IsAlreadyApplied_NeitherStamped_RowIdIsNotAGuid_DefersAsync() =>
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied("not-a-guid", null, _v7(), null))
      .IsFalse().Because("An unparseable position cannot be compared, and a text comparison against it "
        + "would silently succeed and drop the event.");

  [Test]
  public async Task IsAlreadyApplied_NeitherStamped_IncomingIdIsNotTimeOrdered_DefersAsync() =>
    // Incoming id sorts below the row's, so a comparison would report already-applied.
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(
        V7_LATER, null, Guid.Parse(LEGACY_V4_SORTING_BELOW), null))
      .IsFalse().Because("The comparison needs BOTH sides time-ordered. An event carrying a v4 id — one "
        + "produced outside this library's generator — cannot be placed against the row's position.");

  [Test]
  public async Task IsAlreadyApplied_NeitherStamped_EmptyIncomingId_DefersAsync() =>
    await Assert.That(PerspectiveIdempotencyFilter.IsAlreadyApplied(
        TrackedGuid.New().Value.ToString("D"), null, Guid.Empty, null))
      .IsFalse().Because("Guid.Empty is not time-ordered and sorts below everything, so the old comparison "
        + "read it as already applied — a silent drop for any event whose id failed to populate.");
}
