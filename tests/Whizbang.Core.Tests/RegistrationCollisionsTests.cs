// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// The one rule for an item two assemblies both register (#2201): the latest registration wins, and each earlier one
/// from a different assembly is a collision, named with both assemblies.
/// </summary>
/// <code-under-test>src/Whizbang.Core/RegistrationCollision.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/ContributionServiceCollectionExtensions.cs</code-under-test>
public class RegistrationCollisionsTests {
  private static (string, IEnumerable<string>) _c(string assembly, params string[] items) => (assembly, items);

  [Test]
  public async Task TheLatestAssemblyWins_EachEarlierAssemblyIsHiddenAsync() {
    var collisions = RegistrationCollisions.Find("IThing", [_c("Orders", "x", "y"), _c("Billing", "x"), _c("Host", "x")]);

    RegistrationCollision[] expected = [
      new("IThing", "x", "Host", "Billing"),
      new("IThing", "x", "Host", "Orders"),
    ];
    await Assert.That(collisions.SequenceEqual(expected)).IsTrue()
      .Because($"the latest registration of x is used and each earlier assembly's is hidden; found: {string.Join(" | ", collisions)}");
  }

  [Test]
  public async Task AnItemOneAssemblyListsTwice_IsNotACollisionAsync() {
    var collisions = RegistrationCollisions.Find("IThing", [_c("Orders", "x", "x"), _c("Orders", "x")]);

    await Assert.That(collisions).IsEmpty()
      .Because("an assembly that lists an item twice, or is registered twice, hides nothing of another assembly's");
  }

  [Test]
  public async Task AnEarlierAssemblyListingAnItemTwice_IsReportedOnceAsync() {
    var collisions = RegistrationCollisions.Find("IThing", [_c("Orders", "x", "x"), _c("Billing", "x")]);

    await Assert.That(collisions.Count).IsEqualTo(1);
  }

  [Test]
  public async Task DistinctItems_AreNoCollisionAsync() {
    await Assert.That(RegistrationCollisions.Find("IThing", [_c("Orders", "x"), _c("Billing", "y")])).IsEmpty();
  }

  [Test]
  public async Task ACollision_DescribesItself_NamingTheItemAndBothAssembliesAsync() {
    var text = new RegistrationCollision("IThing", "x", "Host", "Orders").ToString();

    await Assert.That(text).IsEqualTo(
      "IThing 'x' is registered by Orders and by Host; Host was registered latest and is used, and Orders's is hidden");
  }

  [Test]
  public async Task Find_NullArguments_ThrowAsync() {
    await Assert.That(() => RegistrationCollisions.Find(null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => RegistrationCollisions.Find("IThing", null!)).Throws<ArgumentNullException>();
  }

  /// <summary>A contribution declared in this test assembly; <see cref="CultureInfo"/> is the other assembly's.</summary>
  private sealed class TestAssemblyFormatProvider : IFormatProvider {
    public object? GetFormat(Type? formatType) => null;
  }

  private sealed class ServedFormatProvider(IReadOnlyList<IFormatProvider> contributions) : IFormatProvider {
    public IReadOnlyList<IFormatProvider> Contributions { get; } = contributions;
    public object? GetFormat(Type? formatType) => null;
  }

  private static ServiceProvider _twoAssemblyContributions(Func<IFormatProvider, IEnumerable<string>>? items) {
    var services = new ServiceCollection();
    services.AddSingleton(CultureInfo.InvariantCulture);
    services.AddSingleton<TestAssemblyFormatProvider>();
    services.AddContribution<IFormatProvider, CultureInfo>("test:format", static c => new ServedFormatProvider(c), items);
    services.AddContribution<IFormatProvider, TestAssemblyFormatProvider>("test:format", static c => new ServedFormatProvider(c), items);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task AContribution_ComposesLatestRegisteredFirstAsync() {
    await using var provider = _twoAssemblyContributions(items: null);

    var served = (ServedFormatProvider)provider.GetRequiredService<IFormatProvider>();

    await Assert.That(served.Contributions[0]).IsTypeOf<TestAssemblyFormatProvider>()
      .Because("the composite asks the latest registration first, so the latest one answers for a shared item");
    await Assert.That(served.Contributions[1]).IsTypeOf<CultureInfo>();
  }

  [Test]
  public async Task AContributionWithItems_ReportsAnItemTwoAssembliesShare_NamingBothAsync() {
    await using var provider = _twoAssemblyContributions(items: static _ => ["shared"]);

    var collisions = provider.GetServices<IRegistrationCollisionSource>().SelectMany(s => s.GetCollisions()).ToList();

    await Assert.That(collisions.Count).IsEqualTo(1);
    await Assert.That(collisions[0]).IsEqualTo(
      new RegistrationCollision("IFormatProvider", "shared", "Whizbang.Core.Tests", "System.Private.CoreLib"));
  }

  [Test]
  public async Task AContributionWithoutItems_AddsNoCollisionSourceAsync() {
    await using var provider = _twoAssemblyContributions(items: null);

    await Assert.That(provider.GetServices<IRegistrationCollisionSource>()).IsEmpty()
      .Because("a registration whose contributions cannot hide each other has nothing to report");
  }
}
