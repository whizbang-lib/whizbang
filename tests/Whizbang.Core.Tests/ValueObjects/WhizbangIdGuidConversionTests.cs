// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// The Guid conversions the generated registration records for each id type, so that an integration
/// (a GraphQL server, say) can turn a Guid into the id without reflection (#2073).
/// </summary>
[NotInParallel("WhizbangIdProviderRegistry")]
public class WhizbangIdGuidConversionTests {
  private static readonly Guid _v4 = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

  [Test]
  public async Task GetGuidConversions_ForAGeneratedId_ReturnsItsFromAsync() {
    var guid = Guid.CreateVersion7();

    var conversions = WhizbangIdProviderRegistry.GetGuidConversions();

    await Assert.That(conversions.ContainsKey(typeof(StrictVersionTestId))).IsTrue();
    await Assert.That(conversions[typeof(StrictVersionTestId)](guid)).IsEqualTo(StrictVersionTestId.From(guid));
  }

  [Test]
  public async Task GetGuidConversions_ForAGeneratedId_KeepsTheIdsVersionRuleAsync() {
    var conversions = WhizbangIdProviderRegistry.GetGuidConversions();
    var strict = conversions[typeof(StrictVersionTestId)];
    var anyVersion = conversions[typeof(AnyVersionTestId)];

    await Assert.That(() => strict(_v4)).Throws<ArgumentException>();
    await Assert.That(anyVersion(_v4)).IsEqualTo(AnyVersionTestId.From(_v4));
    await Assert.That(() => anyVersion(Guid.Empty)).Throws<ArgumentException>();
  }

  [Test]
  public async Task GetGuidConversions_ReturnsASnapshotAsync() {
    var conversions = (IDictionary<Type, Func<Guid, object>>)WhizbangIdProviderRegistry.GetGuidConversions();

    conversions.Remove(typeof(StrictVersionTestId));

    await Assert.That(WhizbangIdProviderRegistry.GetGuidConversions().ContainsKey(typeof(StrictVersionTestId))).IsTrue();
  }

  [Test]
  public async Task RegisterGuidConversion_WithNullConversion_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => WhizbangIdProviderRegistry.RegisterGuidConversion<StrictVersionTestId>(null!))
      .Throws<ArgumentNullException>();
  }
}
