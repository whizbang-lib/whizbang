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
/// Which declared requirements are in force, and how: <b>explicit</b> when the operator set the key, <b>implicit</b>
/// when the key is absent and the setting's declared default turns the feature on. A requirement whose setting
/// does not turn its feature on is not in force at all.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Capabilities/CapabilityActivation.cs</code-under-test>
/// <docs>operations/infrastructure/capabilities#activation</docs>
public class CapabilityActivationResolverTests {
  private static readonly CapabilityId _squeeze = new("postgres", "pg_squeeze");

  private static CapabilityRequirement _enum(string? @default) =>
    new(_squeeze, "Online table rewrite", "W:Maintenance:Strategy", "PgSqueeze", @default);

  private static CapabilityRequirement _bool(string? @default) =>
    new(new CapabilityId("postgres", "pg_stat_statements"), "Index advice", "W:Advisor:Enabled", "true", @default);

  private static CapabilityRequirement _valued(string key, string? @default = null) =>
    new(new CapabilityId("broker", "delayed_exchange"), "Delayed delivery", key, null, @default);

  private static IConfiguration _configuration(params (string Key, string? Value)[] pairs) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
      .Build();

  [Test]
  public async Task KeySetToTheMember_IsExplicitAsync() {
    var active = CapabilityActivationResolver.Resolve([_enum("None")], _configuration(("W:Maintenance:Strategy", "pgsqueeze")));

    await Assert.That(active).Count().IsEqualTo(1);
    await Assert.That(active[0].Activation).IsEqualTo(CapabilityActivation.Explicit);
    await Assert.That(active[0].Key).IsEqualTo("W:Maintenance:Strategy");
    await Assert.That(active[0].Value).IsEqualTo("pgsqueeze").Because("values compare without regard to case, as the binder reads them");
  }

  [Test]
  public async Task KeySetToAnotherMember_IsNotInForce_EvenWhenTheDefaultWouldBeAsync() {
    var active = CapabilityActivationResolver.Resolve([_enum("PgSqueeze")], _configuration(("W:Maintenance:Strategy", "None")));

    await Assert.That(active).IsEmpty().Because("the operator turned the feature off; the default no longer applies");
  }

  [Test]
  public async Task KeyAbsent_DefaultTurnsTheFeatureOn_IsImplicitAsync() {
    var active = CapabilityActivationResolver.Resolve([_enum("PgSqueeze")], _configuration());

    await Assert.That(active).Count().IsEqualTo(1);
    await Assert.That(active[0].Activation).IsEqualTo(CapabilityActivation.Implicit);
    await Assert.That(active[0].Value).IsEqualTo("PgSqueeze");
  }

  [Test]
  public async Task KeyAbsent_DefaultLeavesTheFeatureOff_IsNotInForceAsync() {
    await Assert.That(CapabilityActivationResolver.Resolve([_enum("None"), _enum(null)], _configuration())).IsEmpty();
  }

  [Test]
  public async Task BoolSetting_IsInForceOnlyWhenTrueAsync() {
    var on = CapabilityActivationResolver.Resolve([_bool("false")], _configuration(("W:Advisor:Enabled", "True")));
    var off = CapabilityActivationResolver.Resolve([_bool("true")], _configuration(("W:Advisor:Enabled", "false")));
    var byDefault = CapabilityActivationResolver.Resolve([_bool("true")], _configuration());

    await Assert.That(on.Single().Activation).IsEqualTo(CapabilityActivation.Explicit);
    await Assert.That(off).IsEmpty();
    await Assert.That(byDefault.Single().Activation).IsEqualTo(CapabilityActivation.Implicit);
  }

  [Test]
  public async Task SettingWithoutAValue_IsInForceWhenItHasOneAsync() {
    var set = CapabilityActivationResolver.Resolve([_valued("W:Delay:Exchange")], _configuration(("W:Delay:Exchange", "delayed")));
    var empty = CapabilityActivationResolver.Resolve([_valued("W:Delay:Exchange", "default-exchange")], _configuration(("W:Delay:Exchange", "")));
    var byDefault = CapabilityActivationResolver.Resolve([_valued("W:Delay:Exchange", "default-exchange")], _configuration());
    var emptyDefault = CapabilityActivationResolver.Resolve([_valued("W:Delay:Exchange", "")], _configuration());

    await Assert.That(set.Single().Activation).IsEqualTo(CapabilityActivation.Explicit);
    await Assert.That(empty).IsEmpty().Because("an empty value sets nothing");
    await Assert.That(byDefault.Single().Activation).IsEqualTo(CapabilityActivation.Implicit);
    await Assert.That(emptyDefault).IsEmpty();
  }

  [Test]
  public async Task SectionSetting_IsInForceWhenAnyKeyUnderItIsSetAsync() {
    var active = CapabilityActivationResolver.Resolve([_valued("W:Delay")], _configuration(("W:Delay:Seconds", "30")));

    await Assert.That(active.Single().Activation).IsEqualTo(CapabilityActivation.Explicit);
    await Assert.That(active.Single().Key).IsEqualTo("W:Delay");
    await Assert.That(active.Single().Value).IsNull();
  }

  [Test]
  public async Task WildcardKey_IsInForceOncePerConfiguredEntryThatTurnsItOnAsync() {
    var requirement = new CapabilityRequirement(new CapabilityId("postgres", "vector"), "Vector fields", "W:Databases:*:Vectors", "true", "true");

    var active = CapabilityActivationResolver.Resolve([requirement], _configuration(
      ("W:Databases:orders:Vectors", "true"),
      ("W:Databases:billing:Vectors", "false"),
      ("W:Databases:audit:Name", "audit")));

    await Assert.That(active.Select(a => a.Key)).IsEquivalentTo(["W:Databases:orders:Vectors"])
      .Because("each entry names its own key, and an entry that does not set the key is not turned on by the default");
    await Assert.That(active.Single().Activation).IsEqualTo(CapabilityActivation.Explicit);
  }

  [Test]
  public async Task WildcardKey_WithNoConfiguredEntry_IsNotInForceWhateverTheDefaultAsync() {
    var requirement = new CapabilityRequirement(new CapabilityId("postgres", "vector"), "Vector fields", "W:Databases:*:Vectors", "true", "true");

    await Assert.That(CapabilityActivationResolver.Resolve([requirement], _configuration())).IsEmpty()
      .Because("an entry exists only when configured, so no default can turn one on");
  }

  [Test]
  public async Task SettingWithoutAValueOrADefault_IsNotInForceUntilSetAsync() {
    await Assert.That(CapabilityActivationResolver.Resolve([_valued("W:Delay:Exchange")], _configuration())).IsEmpty();
  }

  [Test]
  public async Task NoConfiguration_LeavesOnlyDefaultsAsync() {
    var active = CapabilityActivationResolver.Resolve([_enum("PgSqueeze"), _bool("false")], configuration: null);

    await Assert.That(active.Single().Requirement.Capability).IsEqualTo(_squeeze);
    await Assert.That(active.Single().Activation).IsEqualTo(CapabilityActivation.Implicit);
  }

  [Test]
  public async Task ActiveRequirement_ReadsAsFeatureKeyValueAndHowItWasTurnedOnAsync() {
    var explicitOne = new ActiveCapabilityRequirement(_enum("None"), "W:Maintenance:Strategy", "PgSqueeze", CapabilityActivation.Explicit);
    var implicitOne = new ActiveCapabilityRequirement(_valued("W:Delay"), "W:Delay", null, CapabilityActivation.Implicit);

    await Assert.That(explicitOne.ToString()).IsEqualTo("Online table rewrite (W:Maintenance:Strategy = PgSqueeze, explicit)");
    await Assert.That(implicitOne.ToString()).IsEqualTo("Delayed delivery (W:Delay, default)");
  }

  [Test]
  public async Task Requirements_AreValidatedAsync() {
    await Assert.That(() => CapabilityActivationResolver.Resolve(null!, null)).Throws<ArgumentNullException>();
  }
}
