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

  /// <summary>
  /// The accepted versions change the generated code, so an id whose option was toggled must not compare equal to
  /// the cached one, or the incremental pipeline would keep the old output.
  /// </summary>
  [Test]
  public async Task SameName_DifferentAcceptAnyVersion_IsNotEqualAsync() {
    var strict = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.ExplicitType);
    var anyVersion = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.ExplicitType, AcceptAnyVersion: true);

    await Assert.That(strict.Equals(anyVersion)).IsFalse();
    await Assert.That(strict.GetHashCode()).IsNotEqualTo(anyVersion.GetHashCode());
  }

  /// <summary>
  /// The pinned id changes the generated code (#2604), so it takes part too: a pin added, changed or removed must not
  /// compare equal to the cached id.
  /// </summary>
  [Test]
  public async Task SameName_DifferentPinnedId_IsNotEqualAsync() {
    var unpinned = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.ExplicitType);
    var pinned = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.ExplicitType, PinnedId: "3f2c8a1e-5b7d-4c9e-a1f0-6d2b8e4c7a91");
    var repinned = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.Property, PinnedId: "7b1d9e2f-0c3a-4e5b-8f6d-1a2b3c4d5e6f");
    var samePin = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.Parameter, PinnedId: "3f2c8a1e-5b7d-4c9e-a1f0-6d2b8e4c7a91");

    await Assert.That(unpinned.Equals(pinned)).IsFalse();
    await Assert.That(pinned.Equals(repinned)).IsFalse();
    await Assert.That(pinned.Equals(samePin)).IsTrue();
    await Assert.That(pinned.GetHashCode()).IsEqualTo(samePin.GetHashCode());
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
