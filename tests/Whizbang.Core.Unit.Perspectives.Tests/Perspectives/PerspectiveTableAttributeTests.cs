// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Helpers;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="PerspectiveTableAttribute"/>: it carries the name as written and goes on a model type.
/// </summary>
/// <docs>fundamentals/perspectives/table-naming#explicit-table-names</docs>
[Category("Core")]
[Category("Attributes")]
public class PerspectiveTableAttributeTests {
  [Test]
  public async Task PerspectiveTableAttribute_Constructor_KeepsTheNameAsWrittenAsync() {
    var attribute = new PerspectiveTableAttribute("wh_per_fulfillment_backlog");

    await Assert.That(attribute.Name).IsEqualTo("wh_per_fulfillment_backlog");
  }

  [Test]
  public async Task PerspectiveTableAttribute_AttributeUsage_OneOnAClassOrStruct_NotInheritedAsync() {
    var usage = AttributeTestHelpers.GetAttributeUsage<PerspectiveTableAttribute>();

    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.ValidOn).IsEqualTo(AttributeTargets.Class | AttributeTargets.Struct);
    await Assert.That(usage.AllowMultiple).IsFalse();
    await Assert.That(usage.Inherited).IsFalse();
  }
}
