using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Coverage round 23 — closes gaps in <see cref="WhizbangId"/>: the <c>From(TrackedGuid)</c>
/// rejection path, and the <see cref="IWhizbangId"/>-typed equality/comparison members that only a
/// cross-type comparison (never the same-struct overloads already covered elsewhere) reaches.
/// </summary>
/// <code-under-test>src/Whizbang.Core/ValueObjects/WhizbangId.cs</code-under-test>
[Category("ValueObjects")]
public class WhizbangIdCoverageTests {

  /// <summary>
  /// If this stopped rejecting a non-time-ordered TrackedGuid, a v4 id could enter a WhizbangId-typed
  /// key and later sort or compare as though it were chronological — corrupting any ordering decision
  /// (cursors, sequencing) that assumes every WhizbangId is a UUIDv7.
  /// </summary>
  [Test]
  public async Task From_TrackedGuid_NotTimeOrdered_ThrowsArgumentExceptionAsync() {
    var v4Tracked = TrackedGuid.NewRandom();

    var exception = await Assert.That(() => WhizbangId.From(v4Tracked))
      .ThrowsExactly<ArgumentException>();
    await Assert.That(exception!.Message).Contains("UUIDv7")
      .Because("the rejection must name the actual requirement so a caller debugging the throw knows what to fix");
  }

  /// <summary>
  /// If cross-type equality stopped comparing by underlying Guid (e.g. started comparing by concrete
  /// struct type instead), two IDs that legitimately reference the same UUIDv7 across two ID types
  /// would stop being recognized as the same identity wherever code holds them behind IWhizbangId.
  /// </summary>
  [Test]
  public async Task EqualsIWhizbangId_SameGuidDifferentIdType_ReturnsTrueAsync() {
    var guid = Guid.CreateVersion7();
    var whizId = WhizbangId.From(guid);
    IWhizbangId other = MessageId.From(guid);

    await Assert.That(((IWhizbangId)whizId).Equals(other)).IsTrue()
      .Because("cross-type IWhizbangId equality is defined by the underlying Guid, not the concrete struct type");
  }

  /// <summary>
  /// If the null guard on this path were ever removed, comparing an IWhizbangId-typed reference
  /// against null would throw a NullReferenceException instead of reporting inequality — crashing
  /// any dictionary lookup or equality check that legitimately passes null.
  /// </summary>
  [Test]
  public async Task EqualsIWhizbangId_WithNull_ReturnsFalseAsync() {
    var whizId = WhizbangId.New();

    await Assert.That(((IWhizbangId)whizId).Equals(null)).IsFalse()
      .Because("a null IWhizbangId must compare unequal, not throw");
  }

  /// <summary>
  /// object.Equals is what boxed comparisons and non-generic collections (Hashtable, ArrayList,
  /// object[]) use; if it stopped delegating to the typed Equals, a WhizbangId stored as object would
  /// silently fail to match itself in exactly those contexts.
  /// </summary>
  [Test]
  public async Task EqualsObject_WithEqualBoxedWhizbangId_ReturnsTrueAsync() {
    var guid = Guid.CreateVersion7();
    var id = WhizbangId.From(guid);
    object boxedSame = WhizbangId.From(guid);

    await Assert.That(id.Equals(boxedSame)).IsTrue();
  }

  /// <summary>
  /// The type-check guard is what stops a WhizbangId from ever reporting itself equal to an unrelated
  /// boxed value; if it regressed, a WhizbangId could match arbitrary objects that merely happen to
  /// share a hash bucket, breaking Dictionary/HashSet lookups keyed by WhizbangId.
  /// </summary>
  [Test]
  public async Task EqualsObject_WithNonWhizbangIdObject_ReturnsFalseAsync() {
    var id = WhizbangId.New();

    await Assert.That(id.Equals((object)"not a WhizbangId")).IsFalse();
  }

  /// <summary>
  /// IComparable ordering (e.g. a SortedSet keyed by IWhizbangId) relies on null sorting after every
  /// real value; if this regressed, a null entry could sort before real IDs and invert chronological
  /// ordering.
  /// </summary>
  [Test]
  public async Task CompareToIWhizbangId_WithNull_ReturnsPositiveAsync() {
    var id = WhizbangId.New();

    await Assert.That(id.CompareTo(other: null)).IsGreaterThan(0);
  }

  /// <summary>
  /// If cross-type comparison stopped delegating to the underlying Guid, sorting a mixed collection
  /// of IWhizbangId values from different ID types would no longer produce chronological order.
  /// </summary>
  [Test]
  public async Task CompareToIWhizbangId_WithOtherIdType_ComparesByGuidAsync() {
    var earlier = WhizbangId.New();
    await Task.Delay(2);
    IWhizbangId later = MessageId.New();

    await Assert.That(earlier.CompareTo(later)).IsLessThan(0);
  }
}
