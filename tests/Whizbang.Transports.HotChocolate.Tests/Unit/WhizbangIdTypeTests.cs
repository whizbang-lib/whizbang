// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate.Language;
using HotChocolate.Types;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Transports.HotChocolate.Tests.Fixtures;
using Whizbang.Transports.HotChocolate.Types;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// The scalar <c>AddWhizbangIds</c> adds for each id type, read and written directly (#2073).
/// </summary>
[Category("Unit")]
[Category("WhizbangId")]
public class WhizbangIdTypeTests {
  private static WhizbangIdType _orderIdType() =>
    new(typeof(GraphQLOrderId), WhizbangIdProviderRegistry.GetGuidConversions()[typeof(GraphQLOrderId)]);

  [Test]
  public async Task Scalar_IsNamedAfterTheIdAndBindsItAsync() {
    var scalar = _orderIdType();

    await Assert.That(scalar.Name).IsEqualTo("GraphQLOrderId");
    await Assert.That(scalar.RuntimeType).IsEqualTo(typeof(GraphQLOrderId));
    await Assert.That(scalar.Description).IsEqualTo("A GraphQLOrderId, written as a UUID string.");
  }

  [Test]
  public async Task IsInstanceOfType_TakesStringsAndNullOnlyAsync() {
    var scalar = _orderIdType();

    await Assert.That(scalar.IsInstanceOfType(new StringValueNode("x"))).IsTrue();
    await Assert.That(scalar.IsInstanceOfType(NullValueNode.Default)).IsTrue();
    await Assert.That(scalar.IsInstanceOfType(new IntValueNode(1))).IsFalse();
  }

  [Test]
  public async Task ParseLiteral_WithNull_ReturnsNullAsync() {
    await Assert.That(_orderIdType().ParseLiteral(NullValueNode.Default)).IsNull();
  }

  [Test]
  public async Task ParseLiteral_WithANonString_ThrowsSerializationExceptionAsync() {
    var scalar = _orderIdType();

    await Assert.That(() => scalar.ParseLiteral(new IntValueNode(1)))
      .Throws<SerializationException>()
      .WithMessageContaining("takes a UUID string");
  }

  [Test]
  public async Task ParseValue_WritesTheIdOrNullAsync() {
    var scalar = _orderIdType();
    var id = GraphQLOrderId.New();

    var written = scalar.ParseValue(id);

    await Assert.That(((StringValueNode)written).Value).IsEqualTo(id.Value.ToString());
    await Assert.That(scalar.ParseValue(null)).IsEqualTo(NullValueNode.Default);
  }

  [Test]
  public async Task ParseValue_WithAnotherIdType_ThrowsSerializationExceptionAsync() {
    var scalar = _orderIdType();

    await Assert.That(() => scalar.ParseValue(GraphQLExternalUserId.New()))
      .Throws<SerializationException>()
      .WithMessageContaining("cannot write this value");
  }

  [Test]
  public async Task ParseValue_WithANonId_ThrowsSerializationExceptionAsync() {
    var scalar = _orderIdType();

    await Assert.That(() => scalar.ParseValue(Guid.CreateVersion7()))
      .Throws<SerializationException>()
      .WithMessageContaining("cannot write this value");
  }

  [Test]
  public async Task TrySerializeAndTryDeserialize_WithAnotherIdType_RefuseItAsync() {
    var scalar = _orderIdType();
    var otherId = GraphQLExternalUserId.New();

    var wrote = scalar.TrySerialize(otherId, out var written);
    var read = scalar.TryDeserialize(otherId, out var readValue);

    await Assert.That(wrote).IsFalse();
    await Assert.That(written).IsNull();
    await Assert.That(read).IsFalse();
    await Assert.That(readValue).IsNull();
  }

  [Test]
  public async Task ParseResult_TakesAStringOrTheIdAsync() {
    var scalar = _orderIdType();
    var id = GraphQLOrderId.New();

    var fromString = scalar.ParseResult(id.Value.ToString());
    var fromId = scalar.ParseResult(id);

    await Assert.That(((StringValueNode)fromString).Value).IsEqualTo(id.Value.ToString());
    await Assert.That(((StringValueNode)fromId).Value).IsEqualTo(id.Value.ToString());
  }

  [Test]
  public async Task TrySerialize_WritesTheIdOrNullAndRefusesAnythingElseAsync() {
    var scalar = _orderIdType();
    var id = GraphQLOrderId.New();

    var wroteId = scalar.TrySerialize(id, out var written);
    var wroteNull = scalar.TrySerialize(null, out var writtenNull);
    var wroteOther = scalar.TrySerialize(id.Value, out var writtenOther);

    await Assert.That(wroteId).IsTrue();
    await Assert.That(written).IsEqualTo(id.Value.ToString());
    await Assert.That(wroteNull).IsTrue();
    await Assert.That(writtenNull).IsNull();
    await Assert.That(wroteOther).IsFalse();
    await Assert.That(writtenOther).IsNull();
  }

  [Test]
  public async Task TryDeserialize_ReadsAStringNullOrTheIdAndRefusesAnythingElseAsync() {
    var scalar = _orderIdType();
    var id = GraphQLOrderId.New();

    var readString = scalar.TryDeserialize(id.Value.ToString(), out var fromString);
    var readNull = scalar.TryDeserialize(null, out var fromNull);
    var readId = scalar.TryDeserialize(id, out var fromId);
    var readOther = scalar.TryDeserialize(42, out var fromOther);

    await Assert.That(readString).IsTrue();
    await Assert.That(fromString).IsEqualTo(id);
    await Assert.That(readNull).IsTrue();
    await Assert.That(fromNull).IsNull();
    await Assert.That(readId).IsTrue();
    await Assert.That(fromId).IsEqualTo(id);
    await Assert.That(readOther).IsFalse();
    await Assert.That(fromOther).IsNull();
  }

  [Test]
  public async Task TryDeserialize_WithAGuidTheIdRefuses_ThrowsWithTheReasonAsync() {
    var scalar = _orderIdType();

    await Assert.That(() => scalar.TryDeserialize("3f2504e0-4f89-41d3-9a0c-0305e82c3301", out _))
      .Throws<SerializationException>()
      .WithMessageContaining("GraphQLOrderId requires UUIDv7");
  }
}
