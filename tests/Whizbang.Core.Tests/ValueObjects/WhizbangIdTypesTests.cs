using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Locks the source-generated value-object API for the most-used IDs
/// (StreamId, EventId). The WhizbangIdGenerator emits identical shape
/// for every Id type — these tests cover the generated From / New /
/// equality / comparison / operator path that the unit suite hadn't
/// been exercising for these specific Ids (MessageId is already covered
/// indirectly through the existing test fixtures).
/// </summary>
public class WhizbangIdTypesTests {

  [Test]
  public async Task StreamId_New_ProducesUuidV7Async() {
    var id = StreamId.New();
    await Assert.That(id.GetIsTimeOrdered()).IsTrue();
  }

  [Test]
  public async Task StreamId_FromTracked_RoundTripsAsync() {
    var tracked = TrackedGuid.NewMedo();
    var id = StreamId.From(tracked);
    await Assert.That(id.Value).IsEqualTo((Guid)tracked);
  }

  [Test]
  public async Task StreamId_FromGuid_RoundTripsAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var id = StreamId.From(g);
    await Assert.That(id.Value).IsEqualTo(g);
    await Assert.That(id.ToGuid()).IsEqualTo(g);
  }

  [Test]
  public async Task StreamId_Equality_SameValueIsEqualAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var a = StreamId.From(g);
    var b = StreamId.From(g);
    await Assert.That(a == b).IsTrue();
    await Assert.That(a != b).IsFalse();
    await Assert.That(a.Equals(b)).IsTrue();
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
  }

  [Test]
  public async Task StreamId_Comparison_FollowsGuidOrderAsync() {
    var a = StreamId.New();
    var b = StreamId.New();
    var cmp = a.CompareTo(b);
    // Same comparison must hold for the wrapped Guid.
    var expected = a.Value.CompareTo(b.Value);
    await Assert.That(Math.Sign(cmp)).IsEqualTo(Math.Sign(expected));
  }

  [Test]
  public async Task StreamId_RelationalOperators_AgreeWithCompareToAsync() {
    var a = StreamId.New();
    await Task.Delay(2);
    var b = StreamId.New();
    var cmp = a.CompareTo(b);
    if (cmp < 0) {
      await Assert.That(a < b).IsTrue();
      await Assert.That(a <= b).IsTrue();
      await Assert.That(a > b).IsFalse();
      await Assert.That(a >= b).IsFalse();
    } else if (cmp > 0) {
      await Assert.That(a > b).IsTrue();
      await Assert.That(a >= b).IsTrue();
    } else {
      await Assert.That(a <= b).IsTrue();
      await Assert.That(a >= b).IsTrue();
    }
  }

  [Test]
  public async Task StreamId_ImplicitToGuid_ExposesUnderlyingValueAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var id = StreamId.From(g);
    Guid asGuid = id;  // implicit
    await Assert.That(asGuid).IsEqualTo(g);
  }

  [Test]
  public async Task StreamId_ExplicitFromGuid_BuildsIdAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var id = (StreamId)g;  // explicit
    await Assert.That(id.Value).IsEqualTo(g);
  }

  [Test]
  public async Task StreamId_ToString_FormatsAsGuidAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var id = StreamId.From(g);
    await Assert.That(id.ToString()).IsEqualTo(g.ToString());
  }

  [Test]
  public async Task StreamId_GetTimestamp_NonDefaultAsync() {
    var id = StreamId.New();
    var ts = id.GetTimestamp();
    await Assert.That(ts).IsGreaterThan(DateTimeOffset.MinValue);
  }

  [Test]
  public async Task EventId_New_RoundTripsAsync() {
    var id = EventId.New();
    var copy = EventId.From(id.Value);
    await Assert.That(id == copy).IsTrue();
    await Assert.That(id.ToGuid()).IsEqualTo(id.Value);
  }

  [Test]
  public async Task EventId_Comparison_TimeOrderedAsync() {
    var a = EventId.New();
    await Task.Delay(2);
    var b = EventId.New();
    await Assert.That(a.CompareTo(b)).IsLessThan(0);
  }

  [Test]
  public async Task EventId_RelationalOperators_AgreeAsync() {
    var a = EventId.New();
    await Task.Delay(2);
    var b = EventId.New();
    await Assert.That(a < b).IsTrue();
    await Assert.That(b > a).IsTrue();
  }
}
