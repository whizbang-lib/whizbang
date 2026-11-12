// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate.Internal;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.HotChocolate.Ids.Tests.Fixtures;
using Whizbang.Transports.HotChocolate.Types;

namespace Whizbang.Transports.HotChocolate.Ids.Tests.Unit;

/// <summary>
/// The type kind the handler predicts, which HotChocolate's filtering and sorting conventions ask for before a type
/// is built (#2073).
/// </summary>
[Category("Unit")]
[Category("WhizbangId")]
public class WhizbangIdTypeDiscoveryHandlerTests {
  private static readonly DefaultTypeInspector _inspector = new();

  [Test]
  public async Task TryInferKind_OfAnId_IsScalarAsync() {
    var typeRef = _inspector.GetTypeRef(typeof(GraphQLOrderId), TypeContext.Input);

    var inferred = new WhizbangIdTypeDiscoveryHandler().TryInferKind(typeRef, new TypeDiscoveryInfo(typeRef), out var kind);

    await Assert.That(inferred).IsTrue();
    await Assert.That(kind).IsEqualTo(TypeKind.Scalar);
  }

  [Test]
  public async Task TryInferKind_OfAnotherType_IsLeftToHotChocolateAsync() {
    var typeRef = _inspector.GetTypeRef(typeof(OrderLookupInput), TypeContext.Input);

    var inferred = new WhizbangIdTypeDiscoveryHandler().TryInferKind(typeRef, new TypeDiscoveryInfo(typeRef), out var kind);

    await Assert.That(inferred).IsFalse();
    await Assert.That(kind).IsEqualTo(default(TypeKind));
  }
}
