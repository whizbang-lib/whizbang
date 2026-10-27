// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// #1755: a removed configuration key is declared where its replacement lives, with
/// <c>[RemovedConfigurationKey]</c>, and the configuration manifest generator records it. At run time the attribute
/// carries what it was given; the startup check reads the generated manifest, never the attribute.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Configuration/RemovedConfigurationKeyAttribute.cs</code-under-test>
/// <docs>operations/configuration/configuration-reference#removed-keys</docs>
public class RemovedConfigurationKeyAttributeTests {
  [Test]
  public async Task Attribute_CarriesTheKeyAndEveryReplacementInOrderAsync() {
    var attribute = new RemovedConfigurationKeyAttribute("Whizbang:Old", "Whizbang:New:A", "Whizbang:New:B");

    await Assert.That(attribute.Key).IsEqualTo("Whizbang:Old");
    await Assert.That(attribute.ReplacedBy).IsEquivalentTo(["Whizbang:New:A", "Whizbang:New:B"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Attribute_WithNoReplacement_HasNoneAsync() {
    await Assert.That(new RemovedConfigurationKeyAttribute("Whizbang:Old").ReplacedBy).IsEmpty()
      .Because("a key can be removed with nothing in its place; the warning then says so");
  }
}
