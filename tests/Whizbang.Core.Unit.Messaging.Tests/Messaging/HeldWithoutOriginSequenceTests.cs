// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2102: a consumer can hold an origin's event without knowing the origin's commit sequence for it (re-attributed
/// history: the origin is recorded, the sequence is not). A windowed fold keyed by that sequence leaves such an event
/// out, so its bucket read short and was reported as a deficit for an event the consumer holds.
/// <see cref="IntegrityDigestMath.PlaceHeld"/> places those events by identity: they belong to the window when the
/// window's digest says so, and they bound the count when it does not.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityDigestMath.cs</code-under-test>
[Category("Core")]
[Category("Messaging")]
public class HeldWithoutOriginSequenceTests {
  private static StreamDigest _digest(long lo, long hi, int count) => new() {
    TenantScope = "t1",
    EventType = "Contracts.Placed",
    StreamId = new Guid("0193a1f2-0000-7000-8000-000000000001"),
    DigestLo = lo,
    DigestHi = hi,
    EventCount = count,
  };

  [Test]
  public async Task PlacedFoldEqualToTheOrigin_MatchesAsync() {
    var placed = _digest(5, 6, 2);

    var placement = IntegrityDigestMath.PlaceHeld(_digest(5, 6, 2), placed, sequenceless: null);

    await Assert.That(placement.Verdict).IsEqualTo(HeldWindowVerdict.Matches);
    await Assert.That(placement.Held).IsSameReferenceAs(placed);
  }

  [Test]
  public async Task EventsHeldWithoutASequence_ThatCompleteTheWindow_MatchAsync() {
    // The origin's window holds {a, b, c}; the consumer has a placed and b, c without a sequence.
    const long A_LO = 0x11, A_HI = 0x21, B_LO = 0x12, B_HI = 0x22, C_LO = 0x14, C_HI = 0x24;
    var origin = _digest(A_LO ^ B_LO ^ C_LO, A_HI ^ B_HI ^ C_HI, 3);

    var placement = IntegrityDigestMath.PlaceHeld(origin, _digest(A_LO, A_HI, 1), _digest(B_LO ^ C_LO, B_HI ^ C_HI, 2));

    await Assert.That(placement.Verdict).IsEqualTo(HeldWindowVerdict.Matches)
      .Because("every event of the window is held; a consumer never reports a deficit for an event it holds");
    await Assert.That(placement.Held!.EventCount).IsEqualTo(3);
  }

  [Test]
  public async Task OnlySequencelessEventsHeld_ThatAreTheWindow_MatchAsync() {
    var placement = IntegrityDigestMath.PlaceHeld(_digest(7, 8, 7), placed: null, _digest(7, 8, 7));

    await Assert.That(placement.Verdict).IsEqualTo(HeldWindowVerdict.Matches)
      .Because("the observed case: the origin holds 7 events, the consumer holds all 7 with no origin sequence");
  }

  [Test]
  public async Task MoreMissingThanTheSequencelessEventsCanCover_IsADeficit_WithTheHeldDigestAsync() {
    // Origin {a, b, c}; the consumer holds a placed and d without a sequence (another window): at least one is missing.
    var placement = IntegrityDigestMath.PlaceHeld(_digest(0x7, 0x7, 3), _digest(0x1, 0x1, 1), _digest(0x40, 0x80, 1));

    await Assert.That(placement.Verdict).IsEqualTo(HeldWindowVerdict.Deficit)
      .Because("three events, at most two held: a genuinely missing event is still a deficit");
    await Assert.That(placement.Held!.DigestLo).IsEqualTo(0x41L)
      .Because("the held digest keeps the sequence-less events, so origin XOR held stays the identity difference");
    await Assert.That(placement.Held.DigestHi).IsEqualTo(0x81L);
    await Assert.That(placement.Held.EventCount).IsEqualTo(2);
    await Assert.That(placement.Held.StreamId).IsEqualTo(_digest(0, 0, 0).StreamId);
  }

  [Test]
  public async Task CountsThatFitButDigestsThatDiffer_AreUndecidedAsync() {
    // Origin {a, b}; held: a placed, c without a sequence. c may belong to another window, b may or may not be here.
    var placement = IntegrityDigestMath.PlaceHeld(_digest(0x3, 0x3, 2), _digest(0x1, 0x1, 1), _digest(0x4, 0x4, 1));

    await Assert.That(placement.Verdict).IsEqualTo(HeldWindowVerdict.Undecided)
      .Because("the window cannot say whether the sequence-less event is the missing one; it is not reported as a deficit");
  }

  [Test]
  public async Task NothingHeldWithoutASequence_ShortPlacedFold_IsADeficitAsBeforeAsync() {
    var placed = _digest(0x1, 0x1, 1);

    var placement = IntegrityDigestMath.PlaceHeld(_digest(0x3, 0x3, 2), placed, _digest(0, 0, 0));

    await Assert.That(placement.Verdict).IsEqualTo(HeldWindowVerdict.Deficit);
    await Assert.That(placement.Held).IsSameReferenceAs(placed);
  }

  [Test]
  public async Task NothingHeldAtAll_IsADeficitAsync() {
    var placement = IntegrityDigestMath.PlaceHeld(_digest(0x3, 0x3, 2), placed: null, sequenceless: null);

    await Assert.That(placement.Verdict).IsEqualTo(HeldWindowVerdict.Deficit);
    await Assert.That(placement.Held).IsNull();
  }

  [Test]
  public async Task NothingHeldWithoutASequence_EqualOrLargerPlacedFold_DiffersAsBeforeAsync() {
    var placement = IntegrityDigestMath.PlaceHeld(_digest(0x3, 0x3, 2), _digest(0x5, 0x3, 2), sequenceless: null);

    await Assert.That(placement.Verdict).IsEqualTo(HeldWindowVerdict.Differs)
      .Because("with nothing sequence-less held, the identity-mismatch and local-surplus taxonomy is unchanged");
  }

  [Test]
  public async Task OnlyTheLowLaneAgreeing_IsNotAMatchAsync() {
    var placement = IntegrityDigestMath.PlaceHeld(_digest(0x3, 0x3, 2), _digest(0x3, 0x9, 2), sequenceless: null);

    await Assert.That(placement.Verdict).IsEqualTo(HeldWindowVerdict.Differs);
  }

  [Test]
  public async Task SequencelessFoldWithOnlyTheLowLaneAgreeing_IsNotAMatchAsync() {
    var placement = IntegrityDigestMath.PlaceHeld(_digest(0x3, 0x3, 2), _digest(0x1, 0x1, 1), _digest(0x2, 0x8, 1));

    await Assert.That(placement.Verdict).IsEqualTo(HeldWindowVerdict.Undecided);
  }
}
