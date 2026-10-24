// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// The three-level <c>AutoProvision</c> switch: <c>Whizbang:Capabilities:&lt;provider&gt;:&lt;name&gt;:AutoProvision</c>
/// wins over <c>Whizbang:Capabilities:&lt;provider&gt;:AutoProvision</c>, which wins over the global
/// <c>Whizbang:Capabilities:AutoProvision</c>; a level that is absent, or not <c>true</c> or <c>false</c>, defers to
/// the next. The answer names the key that decided it, for the verdict line.
/// </summary>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
public class CapabilityAutoProvisionTests {
  private static readonly CapabilityId _squeeze = new("postgres", "pg_squeeze");

  private static IConfiguration _configuration(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  [Test]
  public async Task Keys_NameEachLevelAsync() {
    await Assert.That(CapabilityAutoProvision.GLOBAL_KEY).IsEqualTo("Whizbang:Capabilities:AutoProvision");
    await Assert.That(CapabilityAutoProvision.ProviderKey("postgres")).IsEqualTo("Whizbang:Capabilities:postgres:AutoProvision");
    await Assert.That(CapabilityAutoProvision.CapabilityKey(_squeeze))
      .IsEqualTo("Whizbang:Capabilities:postgres:pg_squeeze:AutoProvision");
  }

  [Test]
  public async Task Global_IsOnByDefaultAsync() {
    var setting = CapabilityAutoProvision.Resolve(_squeeze, new CapabilityOptions(), configuration: null);

    await Assert.That(setting).IsEqualTo(new CapabilityAutoProvisionSetting(true, CapabilityAutoProvision.GLOBAL_KEY));
  }

  [Test]
  public async Task Global_ComesFromTheOptionsAsync() {
    var setting = CapabilityAutoProvision.Resolve(_squeeze, new CapabilityOptions { AutoProvision = false }, _configuration());

    await Assert.That(setting).IsEqualTo(new CapabilityAutoProvisionSetting(false, CapabilityAutoProvision.GLOBAL_KEY));
  }

  [Test]
  public async Task Provider_OverridesGlobalAsync() {
    var setting = CapabilityAutoProvision.Resolve(_squeeze, new CapabilityOptions(),
      _configuration(("Whizbang:Capabilities:postgres:AutoProvision", "false")));

    await Assert.That(setting).IsEqualTo(new CapabilityAutoProvisionSetting(false, "Whizbang:Capabilities:postgres:AutoProvision"));
  }

  [Test]
  public async Task Capability_OverridesProviderAndGlobalAsync() {
    var setting = CapabilityAutoProvision.Resolve(_squeeze, new CapabilityOptions { AutoProvision = false },
      _configuration(
        ("Whizbang:Capabilities:postgres:AutoProvision", "false"),
        ("Whizbang:Capabilities:postgres:pg_squeeze:AutoProvision", "TRUE")));

    await Assert.That(setting).IsEqualTo(new CapabilityAutoProvisionSetting(true, "Whizbang:Capabilities:postgres:pg_squeeze:AutoProvision"));
  }

  [Test]
  public async Task AValueThatIsNotABoolean_DefersToTheNextLevelAsync() {
    var setting = CapabilityAutoProvision.Resolve(_squeeze, new CapabilityOptions(),
      _configuration(
        ("Whizbang:Capabilities:postgres:pg_squeeze:AutoProvision", "sometimes"),
        ("Whizbang:Capabilities:postgres:AutoProvision", "false")));

    await Assert.That(setting.Key).IsEqualTo("Whizbang:Capabilities:postgres:AutoProvision");
    await Assert.That(setting.Enabled).IsFalse();
  }

  [Test]
  public async Task OtherCapabilitiesAndProviders_AreNotAffectedAsync() {
    var configuration = _configuration(
      ("Whizbang:Capabilities:postgres:pg_squeeze:AutoProvision", "false"),
      ("Whizbang:Capabilities:broker:AutoProvision", "false"));

    var setting = CapabilityAutoProvision.Resolve(new CapabilityId("postgres", "vector"), new CapabilityOptions(), configuration);

    await Assert.That(setting).IsEqualTo(new CapabilityAutoProvisionSetting(true, CapabilityAutoProvision.GLOBAL_KEY));
  }

  [Test]
  public async Task Arguments_AreValidatedAsync() {
    await Assert.That(() => CapabilityAutoProvision.Resolve(_squeeze, null!, configuration: null)).Throws<ArgumentNullException>();
  }
}
