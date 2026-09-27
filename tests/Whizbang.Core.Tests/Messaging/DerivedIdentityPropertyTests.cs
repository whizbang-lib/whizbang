using FsCheck;
using FsCheck.Fluent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Helpers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Property tests for <see cref="DerivedIdentity"/>, the layout behind emission and composite-child ids. Events
/// are versioned, claimed and applied in id order, so a derived id must sort where its source sorts, then by its
/// ordinal, and must come out the same every time for the same inputs. <see cref="DerivedIdentityTests"/> pins
/// the layout with fixed examples; these check it for generated sources, ordinals and canonical strings.
/// </summary>
[Category("Core")]
[Category("Messaging")]
[Category("IdGeneration")]
[Category("Property")]
public class DerivedIdentityPropertyTests {
  private const int MAX_ORDINAL_FIELD = 0xFFF;

  /// <summary>Two derivations to compare: each a source, an ordinal (well past the saturation point) and a canonical string.</summary>
  public sealed record DerivationPair(Guid SourceA, Guid SourceB, int OrdinalA, int OrdinalB, string CanonicalA, string CanonicalB);

  private static readonly Gen<Guid> _source = Gen.OneOf(
    ArbMap.Default.GeneratorFor<Guid>(),
    Gen.Constant(Guid.Empty));

  private static readonly Gen<int> _ordinal = Gen.Frequency(
    (4, Gen.Choose(0, 16)),
    (2, Gen.Choose(MAX_ORDINAL_FIELD - 3, MAX_ORDINAL_FIELD + 3)),
    (1, Gen.Choose(0, 10_000)));

  private static readonly Gen<string> _canonical = ArbMap.Default.GeneratorFor<string>().Where(s => s is not null);

  private static readonly Gen<DerivationPair> _pair =
    from sourceA in _source
    from sameSource in Gen.Frequency((2, Gen.Constant(true)), (1, Gen.Constant(false)))
    from sourceB in sameSource ? Gen.Constant(sourceA) : _source
    from ordinalA in _ordinal
    from ordinalB in _ordinal
    from canonicalA in _canonical
    from canonicalB in _canonical
    select new DerivationPair(sourceA, sourceB, ordinalA, ordinalB, canonicalA, canonicalB);

  /// <summary>
  /// For every pair of derivations: each id is repeatable, is version 7 with the RFC variant, keeps its source's
  /// first 80 bits (bar the version and variant bits), and carries its ordinal saturated at 4095. Between the two,
  /// the ids order by source first and then, for one source, by ordinal whenever the lower ordinal is below the
  /// saturation point, whatever the canonical strings hash to.
  /// </summary>
  [Test]
  public async Task FromCanonical_AnyDerivations_AreStableAndSortBySourceThenOrdinalAsync() {
    var property = Prop.ForAll(_pair.ToArbitrary(), pair => _holdsFor(pair));

    await Assert.That(() => PropertyCheck.Run(nameof(DerivedIdentityPropertyTests), property, maxTest: 500, seed: 0x5EED_0D1D))
      .ThrowsNothing();
  }

  private static bool _holdsFor(DerivationPair pair) {
    var a = DerivedIdentity.FromCanonical(pair.SourceA, pair.OrdinalA, pair.CanonicalA);
    var b = DerivedIdentity.FromCanonical(pair.SourceB, pair.OrdinalB, pair.CanonicalB);
    if (a != DerivedIdentity.FromCanonical(pair.SourceA, pair.OrdinalA, pair.CanonicalA)
        || !_layoutHolds(a, pair.SourceA, pair.OrdinalA)
        || !_layoutHolds(b, pair.SourceB, pair.OrdinalB)) {
      return false;
    }

    var aBytes = a.ToByteArray(bigEndian: true);
    var bBytes = b.ToByteArray(bigEndian: true);
    var sourceOrder = aBytes.AsSpan(0, 10).SequenceCompareTo(bBytes.AsSpan(0, 10));
    if (sourceOrder != 0) {
      return Math.Sign(aBytes.AsSpan().SequenceCompareTo(bBytes)) == Math.Sign(sourceOrder);
    }

    var low = Math.Min(pair.OrdinalA, pair.OrdinalB);
    if (pair.OrdinalA == pair.OrdinalB || low >= MAX_ORDINAL_FIELD) {
      return true;
    }
    var aIsLower = pair.OrdinalA < pair.OrdinalB;
    return aBytes.AsSpan().SequenceCompareTo(bBytes) < 0 == aIsLower;
  }

  private static bool _layoutHolds(Guid derived, Guid source, int ordinal) {
    var id = derived.ToByteArray(bigEndian: true);
    var src = source.ToByteArray(bigEndian: true);
    src[6] = (byte)(0x70 | (src[6] & 0x0F));
    src[8] = (byte)(0x80 | (src[8] & 0x3F));

    var ordinalField = (id[10] << 4) | (id[11] >> 4);
    return derived.Version == 7
      && (id[8] >> 6) == 0b10
      && id.AsSpan(0, 10).SequenceEqual(src.AsSpan(0, 10))
      && ordinalField == Math.Min(ordinal, MAX_ORDINAL_FIELD);
  }
}
