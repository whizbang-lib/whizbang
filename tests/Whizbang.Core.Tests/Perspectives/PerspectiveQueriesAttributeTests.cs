// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The declaration surface for which lookups a perspective's queries make.
/// </summary>
/// <remarks>
/// The generator and the analyzer read this attribute from source symbols, where "not written" is
/// distinguishable from <c>false</c>; an instance cannot tell those apart. Both build no index, and
/// the difference only decides which diagnostic a filter that needs one gets (WHIZ308 or WHIZ307).
/// These cases cover what a consuming application touches: each option reads back as written, and
/// the attribute may be inherited but not repeated.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveQueriesAttribute.cs</code-under-test>
[Category("Perspectives")]
public class PerspectiveQueriesAttributeTests {
  [Test]
  public async Task EachOption_ReadsBackAsWrittenAsync() {
    var declaration = new PerspectiveQueriesAttribute { MatchOnAnyField = true, MatchOnMetadata = true };

    await Assert.That(declaration.MatchOnAnyField).IsTrue();
    await Assert.That(declaration.MatchOnMetadata).IsTrue();
  }

  [Test]
  public async Task WithoutOptions_NothingIsSwitchedOnAsync() {
    var declaration = new PerspectiveQueriesAttribute();

    await Assert.That(declaration.MatchOnAnyField).IsFalse()
      .Because("the whole-document index is off unless a model asks for it.");
    await Assert.That(declaration.MatchOnMetadata).IsFalse()
      .Because("the metadata index is off unless a model asks for it.");
  }

  [Test]
  public async Task TheAttribute_IsInheritedAndNotRepeatableAsync() {
    var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
      typeof(PerspectiveQueriesAttribute), typeof(AttributeUsageAttribute))!;

    await Assert.That(usage.Inherited).IsTrue()
      .Because("a base model commonly carries the fields of many models, and its declaration applies to them.");
    await Assert.That(usage.AllowMultiple).IsFalse()
      .Because("one model says one thing about its queries.");
    await Assert.That(usage.ValidOn).IsEqualTo(AttributeTargets.Class | AttributeTargets.Struct);
  }
}
