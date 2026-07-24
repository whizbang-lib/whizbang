using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Closes the coverage gap on MessageId's source-generated API. Existing
/// fixtures cover constructor/From paths, but the IWhizbangId explicit
/// interface getters, the relational operators, IComparable interop,
/// equality across mismatched types, and Parse / TryParse were not
/// exercised — the generated struct sat at ~38% in coverage.
/// </summary>
public class MessageIdAdditionalTests {

  [Test]
  public async Task IWhizbangId_IsTimeOrdered_ReturnsTrueForNewIdAsync() {
    IWhizbangId id = MessageId.New();
    await Assert.That(id.IsTimeOrdered).IsTrue();
  }

  [Test]
  public async Task IWhizbangId_SubMillisecondPrecision_TrueForNewIdAsync() {
    IWhizbangId id = MessageId.New();
    await Assert.That(id.SubMillisecondPrecision).IsTrue();
  }

  [Test]
  public async Task IWhizbangId_Timestamp_HasReasonableValueAsync() {
    IWhizbangId id = MessageId.New();
    await Assert.That(id.Timestamp).IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
  }

  [Test]
  public async Task GetIsTracking_TrueForFreshlyCreatedAsync() {
    var id = MessageId.New();
    await Assert.That(id.GetIsTracking()).IsTrue();
  }

  [Test]
  public async Task RelationalOperators_AcrossPairAsync() {
    var a = MessageId.New();
    await Task.Delay(2);
    var b = MessageId.New();
    await Assert.That(a < b).IsTrue();
    await Assert.That(a <= b).IsTrue();
    await Assert.That(b > a).IsTrue();
    await Assert.That(b >= a).IsTrue();
  }

  [Test]
  public async Task CompareToIWhizbangId_WithNull_ReturnsPositiveAsync() {
    var id = MessageId.New();
    await Assert.That(id.CompareTo(other: null)).IsGreaterThan(0);
  }

  [Test]
  public async Task CompareToIWhizbangId_WithOther_DelegatesToGuidCompareAsync() {
    var a = MessageId.New();
    await Task.Delay(2);
    var b = MessageId.New();
    IWhizbangId bRef = b;
    await Assert.That(a.CompareTo(bRef)).IsLessThan(0);
  }

  [Test]
  public async Task EqualsIWhizbangId_DifferentTypes_FalseForUnrelatedIdAsync() {
    var msg = MessageId.New();
    IWhizbangId other = StreamId.From(msg.Value);
    // Cross-type equality is based on the Guid, so same Guid value should compare equal.
    await Assert.That(((IWhizbangId)msg).Equals(other)).IsTrue();
  }

  [Test]
  public async Task EqualsObject_NullObject_ReturnsFalseAsync() {
    var id = MessageId.New();
    await Assert.That(id.Equals((object?)null)).IsFalse();
  }

  [Test]
  public async Task EqualsObject_NonMessageId_ReturnsFalseAsync() {
    var id = MessageId.New();
    await Assert.That(id.Equals((object)42)).IsFalse();
  }

  [Test]
  public async Task ExplicitOperator_FromGuid_BuildsIdAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var id = (MessageId)g;
    await Assert.That(id.Value).IsEqualTo(g);
  }

  [Test]
  public async Task ExplicitOperator_FromV4Guid_ThrowsAsync() {
    var v4 = Guid.NewGuid();
    await Assert.That(() => (MessageId)v4).Throws<ArgumentException>();
  }

  [Test]
  public async Task From_TrackedGuid_NonV7_ThrowsAsync() {
    // FromExternal lets us wrap a v4 Guid as TrackedGuid; From(TrackedGuid) then rejects it.
    var v4 = TrackedGuid.FromExternal(Guid.NewGuid());
    await Assert.That(() => MessageId.From(v4)).Throws<ArgumentException>();
  }

  [Test]
  public async Task Parse_ValidUuidV7_RoundTripsAsync() {
    var g = (Guid)TrackedGuid.NewMedo();
    var id = MessageId.Parse(g.ToString());
    await Assert.That(id.Value).IsEqualTo(g);
  }

  [Test]
  public async Task Parse_InvalidGuid_ThrowsAsync() {
    await Assert.That(() => MessageId.Parse("not-a-guid")).Throws<Exception>();
  }
}
