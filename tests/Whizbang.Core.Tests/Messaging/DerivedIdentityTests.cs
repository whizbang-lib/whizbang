using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The layout shared by every id the framework derives rather than mints. A derived id must sort where its
/// source sorts: events are versioned, claimed and applied in id order, so a derived id that loses its source's
/// position reorders a stream. The layout keeps the source's first 80 bits (millisecond and monotonic counter),
/// then the ordinal within the derivation, then a hash of the canonical string.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/DerivedIdentity.cs</code-under-test>
[Category("Core")]
[Category("Messaging")]
public class DerivedIdentityTests {
  private static byte[] _bytes(Guid id) => id.ToByteArray(bigEndian: true);

  private static int _compare(Guid a, Guid b) => _bytes(a).AsSpan().SequenceCompareTo(_bytes(b));

  private static int _ordinalField(Guid id) {
    var b = _bytes(id);
    return (b[10] << 4) | (b[11] >> 4);
  }

  [Test]
  public async Task FromCanonical_KeepsTheSourcesFirst80BitsAsync() {
    var source = (Guid)TrackedGuid.NewMedo();

    var derived = DerivedIdentity.FromCanonical(source, 0, "canonical");

    await Assert.That(_bytes(derived).AsSpan(0, 10).SequenceEqual(_bytes(source).AsSpan(0, 10))).IsTrue()
      .Because("the millisecond and the monotonic counter are what order the source; the derived id must carry both");
  }

  [Test]
  public async Task FromCanonical_PutsTheOrdinalInBits80To91Async() {
    var source = (Guid)TrackedGuid.NewMedo();

    await Assert.That(_ordinalField(DerivedIdentity.FromCanonical(source, 0, "c"))).IsEqualTo(0);
    await Assert.That(_ordinalField(DerivedIdentity.FromCanonical(source, 1, "c"))).IsEqualTo(1);
    await Assert.That(_ordinalField(DerivedIdentity.FromCanonical(source, 4095, "c"))).IsEqualTo(4095);
  }

  [Test]
  public async Task FromCanonical_OrdinalPastTheField_SaturatesAndStaysUniqueAsync() {
    var source = (Guid)TrackedGuid.NewMedo();

    var atLimit = DerivedIdentity.FromCanonical(source, 4095, "c|4095");
    var past = DerivedIdentity.FromCanonical(source, 5000, "c|5000");

    await Assert.That(_ordinalField(past)).IsEqualTo(4095)
      .Because("a saturated ordinal still sorts after every ordinal below the limit; wrapping would sort it first");
    await Assert.That(past).IsNotEqualTo(atLimit)
      .Because("uniqueness past the limit comes from the hash, which covers the full ordinal");
  }

  [Test]
  public async Task FromCanonical_SameInputs_SameIdAsync() {
    var source = (Guid)TrackedGuid.NewMedo();

    await Assert.That(DerivedIdentity.FromCanonical(source, 3, "c")).IsEqualTo(DerivedIdentity.FromCanonical(source, 3, "c"));
  }

  [Test]
  public async Task FromCanonical_DifferentCanonical_SameOrdinal_DifferOnlyAfterTheOrdinalAsync() {
    var source = (Guid)TrackedGuid.NewMedo();

    var a = _bytes(DerivedIdentity.FromCanonical(source, 2, "handler-a"));
    var b = _bytes(DerivedIdentity.FromCanonical(source, 2, "handler-b"));

    await Assert.That(a.AsSpan(0, 11).SequenceEqual(b.AsSpan(0, 11))).IsTrue();
    await Assert.That(a.AsSpan(11).SequenceEqual(b.AsSpan(11))).IsFalse();
  }

  [Test]
  public async Task FromCanonical_SourceThatIsNotVersion7_StillYieldsAVersion7IdWithTheRfcVariantAsync() {
    var v4 = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    var derived = DerivedIdentity.FromCanonical(v4, 0, "c");

    await Assert.That(derived.Version).IsEqualTo(7);
    await Assert.That(_bytes(derived)[8] & 0xC0).IsEqualTo(0x80);
  }

  [Test]
  public async Task FromCanonical_ConsecutiveSources_DerivedIdsSortInSourceOrderAsync() {
    // Consecutive ids from the generator, most of them inside one millisecond: exactly the case where only the
    // counter orders them. Whatever the canonical string, the derived ids must sort the way the sources do.
    const int count = 10_000;
    var sources = Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();
    int outOfOrder = 0;

    for (int i = 1; i < count; i++) {
      var earlier = DerivedIdentity.FromCanonical(sources[i - 1], 0, $"z-handler-{i}");
      var later = DerivedIdentity.FromCanonical(sources[i], 0, $"a-handler-{i}");
      if (_compare(later, earlier) <= 0) {
        outOfOrder++;
      }
    }

    await Assert.That(outOfOrder).IsEqualTo(0);
  }

  [Test]
  public async Task FromCanonical_OneSource_DerivedIdsSortInOrdinalOrderAsync() {
    var source = (Guid)TrackedGuid.NewMedo();
    int outOfOrder = 0;
    var previous = DerivedIdentity.FromCanonical(source, 0, "c|0");

    for (int ordinal = 1; ordinal < 4096; ordinal++) {
      var next = DerivedIdentity.FromCanonical(source, ordinal, $"c|{ordinal}");
      if (_compare(next, previous) <= 0) {
        outOfOrder++;
      }
      previous = next;
    }

    await Assert.That(outOfOrder).IsEqualTo(0);
  }

  [Test]
  public async Task FromCanonical_EveryIdOfAnEarlierSource_SortsBeforeEveryIdOfALaterOneAsync() {
    // A handling that emits several events must not interleave with the next handling's events.
    var first = (Guid)TrackedGuid.NewMedo();
    var second = (Guid)TrackedGuid.NewMedo();

    var lastOfFirst = DerivedIdentity.FromCanonical(first, 4095, "c");
    var firstOfSecond = DerivedIdentity.FromCanonical(second, 0, "c");

    await Assert.That(_compare(firstOfSecond, lastOfFirst)).IsGreaterThan(0);
  }
}
