using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Coverage-round tests for TrackedGuid, targeting the object-typed Equals override, the
/// mixed Guid/TrackedGuid comparison operators, and the two escape hatches of Timestamp
/// extraction (a non-v7 guid, and a v7-tagged guid whose timestamp cannot be represented as a
/// DateTimeOffset) that the main TrackedGuidTests suite does not exercise.
/// </summary>
/// <tests>src/Whizbang.Core/ValueObjects/TrackedGuid.cs</tests>
[Category("ValueObjects")]
public class TrackedGuidCoverageTests {
  // Ids round-trip through boxed/object-typed paths (e.g. object-keyed dictionaries,
  // heterogeneous collections); if the object-typed override diverged from the typed Equals,
  // two ids over the same underlying Guid could be treated as distinct once boxed, silently
  // duplicating what should be a single identity.
  [Test]
  public async Task Equals_WithBoxedTrackedGuid_ComparesUnderlyingValueAsync() {
    // Arrange
    var guid = Guid.CreateVersion7();
    var tracked = TrackedGuid.FromExternal(guid);
    object boxedSame = TrackedGuid.FromExternal(guid);
    object boxedDifferent = TrackedGuid.NewRandom();

    // Act & Assert
    await Assert.That(tracked.Equals(boxedSame)).IsTrue()
      .Because("two TrackedGuids over the same Guid value must compare equal even when boxed");
    await Assert.That(tracked.Equals(boxedDifferent)).IsFalse()
      .Because("a different underlying Guid must never compare equal");
  }

  // An object that is not a TrackedGuid at all must fail the type check rather than throw --
  // that is what protects a caller who boxes ids into a heterogeneous collection.
  [Test]
  public async Task Equals_WithNonTrackedGuidObject_ReturnsFalseAsync() {
    // Arrange
    var tracked = TrackedGuid.NewMedo();

    // Act & Assert
    await Assert.That(tracked.Equals((object)"not-a-tracked-guid")).IsFalse();
    await Assert.That(tracked.Equals((object?)null)).IsFalse();
  }

  // Ids are frequently compared against raw Guids at storage/API boundaries (e.g. a stored
  // Guid column compared to a TrackedGuid held in memory); if either mixed-type operator
  // regressed, a matching id could read as a mismatch and a lookup would wrongly miss.
  [Test]
  public async Task ComparisonOperators_BetweenGuidAndTrackedGuid_AgreeWithUnderlyingValueAsync() {
    // Arrange
    var guid = Guid.CreateVersion7();
    var tracked = TrackedGuid.FromExternal(guid);
    var otherGuid = Guid.NewGuid();

    // Act & Assert - Guid != TrackedGuid
    await Assert.That(guid != tracked).IsFalse()
      .Because("the same underlying value must never compare unequal");
    await Assert.That(otherGuid != tracked).IsTrue()
      .Because("a different underlying value must compare unequal, in either operand order");

    // Act & Assert - TrackedGuid == Guid / TrackedGuid != Guid
    await Assert.That(tracked == guid).IsTrue();
    await Assert.That(tracked != guid).IsFalse();
    await Assert.That(tracked != otherGuid).IsTrue();
  }

  // Timestamp is meaningless for a v4 (random) id; a caller that forgot to check
  // IsTimeOrdered first must get an unambiguous sentinel (MinValue), never a fabricated
  // "recent" time derived from what are actually random bits.
  [Test]
  public async Task Timestamp_OnNonV7Guid_ReturnsMinValueAsync() {
    // Arrange
    var tracked = TrackedGuid.NewRandom();

    // Act
    var timestamp = tracked.Timestamp;

    // Assert
    await Assert.That(timestamp).IsEqualTo(DateTimeOffset.MinValue)
      .Because("a v4 Guid carries no UUIDv7 timestamp field to extract");
  }

  // A v7-tagged Guid is not guaranteed to carry a timestamp Medo's Uuid7 can render as a
  // DateTimeOffset: with the 48-bit timestamp field maxed out, the millisecond count is far
  // larger than DateTimeOffset can represent (year 9999), and the underlying conversion
  // overflows. If the try/catch here regressed, that overflow would propagate out of a plain
  // property getter and crash any caller who merely inspected .Timestamp on an externally
  // sourced id -- it must fail safe to MinValue, the same as a non-v7 guid.
  [Test]
  public async Task Timestamp_OnV7GuidWithUnrepresentableTimestamp_ReturnsMinValueAsync() {
    // Arrange - version nibble "7" with the 48-bit timestamp field maxed out (0xFFFFFFFFFFFF),
    // which is far beyond what DateTimeOffset can represent (max year 9999).
    var guid = Guid.Parse("ffffffff-ffff-7fff-8fff-ffffffffffff");
    await Assert.That(guid.Version).IsEqualTo(7)
      .Because("the fixture must exercise the v7 branch, not the non-v7 short-circuit");
    var tracked = TrackedGuid.FromExternal(guid);

    // Act
    var timestamp = tracked.Timestamp;

    // Assert
    await Assert.That(timestamp).IsEqualTo(DateTimeOffset.MinValue)
      .Because("an unrepresentable timestamp must fail safe instead of throwing out of a property getter");
  }
}
