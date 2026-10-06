// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A discovered id is identified by its fully qualified name alone, which is what lets the generator
/// collapse the same id found through a type, a property and a parameter into one, and never equal
/// to no id at all.
/// </summary>
/// <tests>src/Whizbang.Generators/WhizbangIdInfo.cs</tests>
public class WhizbangIdInfoEqualityTests {
  [Test]
  public async Task SameName_IsEqualWhateverTheDiscoverySourceAsync() {
    var fromType = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.ExplicitType);
    var fromProperty = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.Property, SuppressDuplicateWarning: true);

    await Assert.That(fromType.Equals(fromProperty)).IsTrue();
    await Assert.That(fromType.GetHashCode()).IsEqualTo(fromProperty.GetHashCode());
  }

  [Test]
  public async Task DifferentName_IsNotEqualAsync() {
    var shop = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.ExplicitType);
    var billing = new WhizbangIdInfo("OrderId", "Billing", DiscoverySource.ExplicitType);

    await Assert.That(shop.Equals(billing)).IsFalse();
  }

  [Test]
  public async Task NoId_IsNotEqualAsync() {
    var shop = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.ExplicitType);

    await Assert.That(shop.Equals(null)).IsFalse();
  }
}
