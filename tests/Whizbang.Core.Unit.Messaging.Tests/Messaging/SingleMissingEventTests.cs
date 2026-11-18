// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2103: a stream bucket's id lanes are the XOR of its event ids' two 64-bit halves, so when the origin's bucket holds
/// exactly one event more than the consumer's, origin XOR local IS the missing event's id. The repair then names that one
/// event instead of re-shipping the stream. In every other case there is no single id to name.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityDigestMath.cs</code-under-test>
[Category("Core")]
[Category("Messaging")]
public class SingleMissingEventTests {
  private static readonly Guid _a = new("0193a1f2-1111-7000-8000-00000000000a");
  private static readonly Guid _b = new("0193a1f2-2222-7000-8000-00000000000b");
  private static readonly Guid _c = new("0193a1f2-3333-7000-8000-00000000000c");

  private static StreamDigest _bucket(int count, params Guid[] ids) {
    long lo = 0, hi = 0;
    foreach (var id in ids) {
      var bytes = id.ToByteArray(bigEndian: true);
      lo ^= BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8));
      hi ^= BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8, 8));
    }
    return new StreamDigest {
      TenantScope = "t1",
      EventType = "Contracts.Placed",
      StreamId = _a,
      // A stand-in for the hash lanes that, like them, differs for different sets.
      DigestLo = lo ^ 0x5555,
      DigestHi = hi ^ 0x6666,
      EventCount = count,
      IdLo = lo,
      IdHi = hi,
    };
  }

  [Test]
  public async Task OneEventMissing_IsNamedByTheXorOfTheIdLanesAsync() {
    await Assert.That(IntegrityDigestMath.SingleMissingEventId(_bucket(3, _a, _b, _c), _bucket(2, _a, _c))).IsEqualTo(_b);
  }

  [Test]
  public async Task NothingHeldLocally_AndTheOriginHoldsOne_NamesItAsync() {
    await Assert.That(IntegrityDigestMath.SingleMissingEventId(_bucket(1, _b), held: null)).IsEqualTo(_b);
  }

  [Test]
  public async Task TwoEventsMissing_NameNothingAsync() {
    await Assert.That(IntegrityDigestMath.SingleMissingEventId(_bucket(3, _a, _b, _c), _bucket(1, _a))).IsNull()
      .Because("the XOR of two missing ids is no event's id; the stream syncs whole");
  }

  [Test]
  public async Task NothingMissing_OrALocalSurplus_NamesNothingAsync() {
    await Assert.That(IntegrityDigestMath.SingleMissingEventId(_bucket(2, _a, _b), _bucket(2, _a, _c))).IsNull();
    await Assert.That(IntegrityDigestMath.SingleMissingEventId(_bucket(1, _a), _bucket(2, _a, _b))).IsNull()
      .Because("a surplus is never repaired by asking the origin");
  }

  [Test]
  public async Task EitherSideWithoutIdLanes_NamesNothingAsync() {
    await Assert.That(IntegrityDigestMath.SingleMissingEventId(_bucket(3, _a, _b, _c) with { IdLo = null }, _bucket(2, _a, _c))).IsNull()
      .Because("an older origin, or a bucket not yet backfilled, falls back to whole-stream sync");
    await Assert.That(IntegrityDigestMath.SingleMissingEventId(_bucket(3, _a, _b, _c), _bucket(2, _a, _c) with { IdHi = null })).IsNull();
  }

  [Test]
  public async Task AnXorOfZero_NamesNothingAsync() {
    await Assert.That(IntegrityDigestMath.SingleMissingEventId(_bucket(2, _a), _bucket(1, _a))).IsNull()
      .Because("the empty id is no event");
  }

  [Test]
  public async Task PlacingHeldEvents_CarriesTheirIdLanes_SoTheMissingOneIsStillNamedAsync() {
    // Origin {a, b, c}; placed {a}; held without a sequence {c}: b is missing, named through the combined lanes.
    var placement = IntegrityDigestMath.PlaceHeld(_bucket(3, _a, _b, _c), _bucket(1, _a), _bucket(1, _c));

    await Assert.That(placement.Verdict).IsEqualTo(HeldWindowVerdict.Deficit);
    await Assert.That(IntegrityDigestMath.SingleMissingEventId(_bucket(3, _a, _b, _c), placement.Held)).IsEqualTo(_b);
  }

  [Test]
  public async Task PlacingHeldEvents_WithoutIdLanesOnEitherPart_LeavesNoneAsync() {
    var placement = IntegrityDigestMath.PlaceHeld(_bucket(3, _a, _b, _c), _bucket(1, _a) with { IdLo = null }, _bucket(1, _c));

    await Assert.That(placement.Held!.IdLo).IsNull();
    await Assert.That(IntegrityDigestMath.PlaceHeld(_bucket(3, _a, _b, _c), _bucket(1, _a), _bucket(1, _c) with { IdHi = null }).Held!.IdHi)
      .IsNull();
    await Assert.That(IntegrityDigestMath.PlaceHeld(_bucket(3, _a, _b, _c), placed: null, _bucket(1, _c)).Held!.IdLo)
      .IsEqualTo(_bucket(1, _c).IdLo).Because("nothing placed folds as zero");
  }
}
