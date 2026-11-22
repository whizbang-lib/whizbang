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

  /// <summary>An id whose version allow-list changed generates different code, so it must regenerate (#2516).</summary>
  [Test]
  public async Task SameName_DifferentVersions_IsNotEqualAsync() {
    var strict = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.ExplicitType);
    var v7OrV5 = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.ExplicitType, Versions: (1 << 7) | (1 << 5));

    await Assert.That(strict.Equals(v7OrV5)).IsFalse();
    await Assert.That(strict.GetHashCode()).IsNotEqualTo(v7OrV5.GetHashCode());
  }

  /// <summary>Whether <c>Versions</c> is written decides whether WHIZ038 is reported, so it takes part too.</summary>
  [Test]
  public async Task SameName_DifferentDeclaresVersions_IsNotEqualAsync() {
    var unwritten = new WhizbangIdInfo("OrderId", "Shop", DiscoverySource.ExplicitType, AcceptAnyVersion: true);
    var written = new WhizbangIdInfo(
      "OrderId", "Shop", DiscoverySource.ExplicitType, AcceptAnyVersion: true, DeclaresVersions: true);

    await Assert.That(unwritten.Equals(written)).IsFalse();
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
