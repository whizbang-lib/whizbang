// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The edges of the attribute-argument name conversion: a missing name, and an uppercase letter
/// that ends the name (no following character to decide an acronym boundary by).
/// </summary>
/// <tests>src/Whizbang.Generators/Utilities/AttributeArgNamingHelper.cs</tests>
public class AttributeArgNamingHelperBranchTests {
  [Test]
  public async Task Convert_NoName_IsEmptyAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert(null!, AttributeArgNamingConvention.SnakeCase)).IsEqualTo("");
  }

  [Test]
  [Arguments(AttributeArgNamingConvention.SnakeCase, "value_x")]
  [Arguments(AttributeArgNamingConvention.KebabCase, "value-x")]
  [Arguments(AttributeArgNamingConvention.UpperSnake, "VALUE_X")]
  public async Task Convert_TrailingUppercase_StartsItsOwnTokenAsync(AttributeArgNamingConvention convention, string expected) {
    await Assert.That(AttributeArgNamingHelper.Convert("valueX", convention)).IsEqualTo(expected);
  }

  [Test]
  public async Task Convert_TrailingAcronym_StaysOneTokenAsync() {
    await Assert.That(AttributeArgNamingHelper.Convert("regionID", AttributeArgNamingConvention.SnakeCase)).IsEqualTo("region_id")
      .Because("the D follows an uppercase letter and ends the name, so nothing makes it a new token");
  }
}
