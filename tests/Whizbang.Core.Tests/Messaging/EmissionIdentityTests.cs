using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A retry is not a republish: the id of an event a handler emits must be a function of the handling,
/// so a redelivered inbox row re-derives the same ids and the store's primary keys absorb the copy.
/// These tests pin the derivation (which inputs matter, which do not) and the UUIDv8 layout.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/EmissionIdentity.cs</code-under-test>
[Category("Core")]
[Category("Messaging")]
public class EmissionIdentityTests {
  private const string SERVICE = "orders";
  private const string HANDLER = "OrderPlacedHandler";
  private const string TYPE = "Contracts.Orders.OrderPlaced";

  [Test]
  public async Task Derive_SameInputs_ReturnsSameIdAsync() {
    var source = (Guid)TrackedGuid.NewMedo();

    var first = EmissionIdentity.Derive(source, SERVICE, HANDLER, TYPE, ordinal: 0);
    var second = EmissionIdentity.Derive(source, SERVICE, HANDLER, TYPE, ordinal: 0);

    await Assert.That(second).IsEqualTo(first)
      .Because("a retry of the same handling must produce the same emission id or the retry becomes a republish");
  }

  [Test]
  public async Task Derive_DifferentOrdinal_ReturnsDifferentIdAsync() {
    var source = (Guid)TrackedGuid.NewMedo();

    var zeroth = EmissionIdentity.Derive(source, SERVICE, HANDLER, TYPE, ordinal: 0);
    var first = EmissionIdentity.Derive(source, SERVICE, HANDLER, TYPE, ordinal: 1);

    await Assert.That(first).IsNotEqualTo(zeroth)
      .Because("one handling may emit the same type twice; the two emissions are distinct messages");
  }

  [Test]
  public async Task Derive_DifferentHandler_ReturnsDifferentIdAsync() {
    var source = (Guid)TrackedGuid.NewMedo();

    var a = EmissionIdentity.Derive(source, SERVICE, "FirstHandler", TYPE, ordinal: 0);
    var b = EmissionIdentity.Derive(source, SERVICE, "SecondHandler", TYPE, ordinal: 0);

    await Assert.That(b).IsNotEqualTo(a)
      .Because("two handler rows of one message in one service each emit their own events");
  }

  [Test]
  public async Task Derive_NullHandlerVersusNamedHandler_ReturnsDifferentIdAsync() {
    var source = (Guid)TrackedGuid.NewMedo();

    var unnamed = EmissionIdentity.Derive(source, SERVICE, handlerName: null, TYPE, ordinal: 0);
    var named = EmissionIdentity.Derive(source, SERVICE, HANDLER, TYPE, ordinal: 0);

    await Assert.That(named).IsNotEqualTo(unnamed);
  }

  [Test]
  public async Task Derive_DifferentService_ReturnsDifferentIdAsync() {
    var source = (Guid)TrackedGuid.NewMedo();

    var a = EmissionIdentity.Derive(source, "orders", HANDLER, TYPE, ordinal: 0);
    var b = EmissionIdentity.Derive(source, "billing", HANDLER, TYPE, ordinal: 0);

    await Assert.That(b).IsNotEqualTo(a)
      .Because("two services handling the same message must not collide on the wire");
  }

  [Test]
  public async Task Derive_DifferentEmittedType_ReturnsDifferentIdAsync() {
    var source = (Guid)TrackedGuid.NewMedo();

    var a = EmissionIdentity.Derive(source, SERVICE, HANDLER, "Contracts.Orders.OrderPlaced", ordinal: 0);
    var b = EmissionIdentity.Derive(source, SERVICE, HANDLER, "Contracts.Orders.OrderPriced", ordinal: 0);

    await Assert.That(b).IsNotEqualTo(a);
  }

  [Test]
  public async Task Derive_DifferentSource_ReturnsDifferentIdAsync() {
    var a = EmissionIdentity.Derive((Guid)TrackedGuid.NewMedo(), SERVICE, HANDLER, TYPE, ordinal: 0);
    var b = EmissionIdentity.Derive((Guid)TrackedGuid.NewMedo(), SERVICE, HANDLER, TYPE, ordinal: 0);

    await Assert.That(b).IsNotEqualTo(a);
  }

  [Test]
  public async Task Derive_ResultIsVersion7ShapedWithRfcVariantAsync() {
    var derived = EmissionIdentity.Derive((Guid)TrackedGuid.NewMedo(), SERVICE, HANDLER, TYPE, ordinal: 3);

    await Assert.That(derived.Version).IsEqualTo(7)
      .Because("the framework's id value objects accept only time-ordered v7 ids; a derived id must pass the same gate as a minted one");
    var bytes = derived.ToByteArray(bigEndian: true);
    await Assert.That(bytes[8] & 0xC0).IsEqualTo(0x80)
      .Because("the RFC variant bits must be 10xx like every other framework id");
  }

  [Test]
  public async Task Derive_ResultIsAcceptedByTheMessageIdValueObjectAsync() {
    var derived = EmissionIdentity.Derive((Guid)TrackedGuid.NewMedo(), SERVICE, HANDLER, TYPE, ordinal: 0);

    var messageId = MessageId.From(derived);

    await Assert.That(messageId.Value).IsEqualTo(derived);
  }

  [Test]
  public async Task Derive_InheritsFirst48BitsOfSourceAsync() {
    var source = (Guid)TrackedGuid.NewMedo();
    var derived = EmissionIdentity.Derive(source, SERVICE, HANDLER, TYPE, ordinal: 0);

    var sourceBytes = source.ToByteArray(bigEndian: true);
    var derivedBytes = derived.ToByteArray(bigEndian: true);

    await Assert.That(derivedBytes.AsSpan(0, 6).SequenceEqual(sourceBytes.AsSpan(0, 6))).IsTrue()
      .Because("the derived id keeps its source's millisecond prefix so it stays time-local in indexes");
  }

  [Test]
  public async Task Derive_TwoOrdinals_ShareThePrefixAndDifferInTheHashAsync() {
    var source = (Guid)TrackedGuid.NewMedo();
    var a = EmissionIdentity.Derive(source, SERVICE, HANDLER, TYPE, ordinal: 0).ToByteArray(bigEndian: true);
    var b = EmissionIdentity.Derive(source, SERVICE, HANDLER, TYPE, ordinal: 1).ToByteArray(bigEndian: true);

    await Assert.That(a.AsSpan(0, 6).SequenceEqual(b.AsSpan(0, 6))).IsTrue();
    await Assert.That(a.AsSpan(6).SequenceEqual(b.AsSpan(6))).IsFalse();
  }

  [Test]
  public async Task Derive_EmptySource_ThrowsAsync() {
    await Assert.That(() => EmissionIdentity.Derive(Guid.Empty, SERVICE, HANDLER, TYPE, ordinal: 0))
      .Throws<ArgumentException>()
      .Because("with no source there is no handling to derive from; the caller must mint a fresh id instead");
  }

  [Test]
  public async Task Derive_NegativeOrdinal_ThrowsAsync() {
    await Assert.That(() => EmissionIdentity.Derive((Guid)TrackedGuid.NewMedo(), SERVICE, HANDLER, TYPE, ordinal: -1))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Derive_NullServiceName_ThrowsAsync() {
    await Assert.That(() => EmissionIdentity.Derive((Guid)TrackedGuid.NewMedo(), null!, HANDLER, TYPE, ordinal: 0))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Derive_NullEmittedTypeName_ThrowsAsync() {
    await Assert.That(() => EmissionIdentity.Derive((Guid)TrackedGuid.NewMedo(), SERVICE, HANDLER, null!, ordinal: 0))
      .Throws<ArgumentNullException>();
  }

  // ---- EmissionSequence ----

  [Test]
  public async Task Sequence_SameKey_HandsOutConsecutiveOrdinalsFromZeroAsync() {
    var key = new object();

    var first = EmissionSequence.Next(key);
    var second = EmissionSequence.Next(key);
    var third = EmissionSequence.Next(key);

    await Assert.That(first).IsEqualTo(0);
    await Assert.That(second).IsEqualTo(1);
    await Assert.That(third).IsEqualTo(2);
  }

  [Test]
  public async Task Sequence_DifferentKeys_CountIndependentlyAsync() {
    var a = new object();
    var b = new object();

    _ = EmissionSequence.Next(a);
    _ = EmissionSequence.Next(a);
    var fromB = EmissionSequence.Next(b);

    await Assert.That(fromB).IsEqualTo(0)
      .Because("a retry materializes a fresh envelope instance; its sequence must start over so the ids line up");
  }

  [Test]
  public async Task Sequence_NullKey_ThrowsAsync() {
    await Assert.That(() => EmissionSequence.Next(null!)).Throws<ArgumentNullException>();
  }
}
