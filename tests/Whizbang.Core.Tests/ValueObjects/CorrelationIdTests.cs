using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Mirror of MessageIdAdditionalTests for the source-generated
/// CorrelationId. Same generator emit; the additional surface (operators,
/// IWhizbangId, Parse, etc.) sat at ~38% in coverage.
/// </summary>
public class CorrelationIdTests {

  [Test]
  public async Task New_ProducesUuidV7Async() {
    var id = CorrelationId.New();
    await Assert.That(id.GetIsTimeOrdered()).IsTrue();
    await Assert.That(id.GetIsTracking()).IsTrue();
  }

  [Test]
  public async Task From_NonV7Guid_ThrowsAsync() {
    var v4 = Guid.NewGuid();
    await Assert.That(() => CorrelationId.From(v4)).Throws<ArgumentException>();
  }

  [Test]
  public async Task From_TrackedGuid_RoundTripsAsync() {
    var tracked = TrackedGuid.NewMedo();
    var id = CorrelationId.From(tracked);
    await Assert.That(id.Value).IsEqualTo((Guid)tracked);
  }

  [Test]
  public async Task Equality_SameValueIsEqualAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var a = CorrelationId.From(g);
    var b = CorrelationId.From(g);
    await Assert.That(a == b).IsTrue();
    await Assert.That(a != b).IsFalse();
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
  }

  [Test]
  public async Task RelationalOperators_TimeOrderedSequenceAsync() {
    var a = CorrelationId.New();
    await Task.Delay(2);
    var b = CorrelationId.New();
    await Assert.That(a < b).IsTrue();
    await Assert.That(a <= b).IsTrue();
    await Assert.That(b > a).IsTrue();
    await Assert.That(b >= a).IsTrue();
  }

  [Test]
  public async Task CompareToIWhizbangId_WithNull_PositiveAsync() {
    var id = CorrelationId.New();
    await Assert.That(id.CompareTo(other: null)).IsGreaterThan(0);
  }

  [Test]
  public async Task IWhizbangId_InterfaceGetters_ExposeUnderlyingTrackingAsync() {
    IWhizbangId id = CorrelationId.New();
    await Assert.That(id.IsTimeOrdered).IsTrue();
    await Assert.That(id.SubMillisecondPrecision).IsTrue();
    await Assert.That(id.Timestamp).IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
  }

  [Test]
  public async Task EqualsObject_Branches_ReturnExpectedAsync() {
    var id = CorrelationId.New();
    await Assert.That(id.Equals((object)id)).IsTrue();
    await Assert.That(id.Equals((object?)null)).IsFalse();
    await Assert.That(id.Equals((object)"not-an-id")).IsFalse();
  }

  [Test]
  public async Task ImplicitToGuid_ExposesUnderlyingAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var id = CorrelationId.From(g);
    Guid asGuid = id;
    await Assert.That(asGuid).IsEqualTo(g);
  }

  [Test]
  public async Task ExplicitFromGuid_BuildsIdAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var id = (CorrelationId)g;
    await Assert.That(id.Value).IsEqualTo(g);
  }

  [Test]
  public async Task ExplicitFromV4Guid_ThrowsAsync() {
    var v4 = Guid.NewGuid();
    await Assert.That(() => (CorrelationId)v4).Throws<ArgumentException>();
  }

  [Test]
  public async Task ToString_FormatsAsGuidAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var id = CorrelationId.From(g);
    await Assert.That(id.ToString()).IsEqualTo(g.ToString());
  }

  [Test]
  public async Task Parse_ValidUuidV7_RoundTripsAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var id = CorrelationId.Parse(g.ToString());
    await Assert.That(id.Value).IsEqualTo(g);
  }
}
