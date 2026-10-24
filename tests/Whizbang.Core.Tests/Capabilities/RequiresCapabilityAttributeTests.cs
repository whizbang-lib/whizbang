// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// The two ways a setting declares the capability it needs: any provider's capability by provider and name, or a
/// Postgres extension by its name alone. The generator reads them; at run time they carry what they were given.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Capabilities/RequiresCapabilityAttribute.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Capabilities/CapabilityRequirement.cs</code-under-test>
/// <docs>operations/infrastructure/capabilities#declaring-a-requirement</docs>
public class RequiresCapabilityAttributeTests {
  [Test]
  public async Task RequiresCapability_CarriesProviderNameAndFeatureAsync() {
    var attribute = new RequiresCapabilityAttribute("broker", "delayed_exchange") { Feature = "Delayed delivery" };

    await Assert.That(attribute.Provider).IsEqualTo("broker");
    await Assert.That(attribute.Name).IsEqualTo("delayed_exchange");
    await Assert.That(attribute.Feature).IsEqualTo("Delayed delivery");
  }

  [Test]
  public async Task RequiresPostgresExtension_IsTheExtensionUnderThePostgresProviderAsync() {
    var attribute = new RequiresPostgresExtensionAttribute("pg_squeeze");

    await Assert.That(attribute.Extension).IsEqualTo("pg_squeeze");
    await Assert.That(attribute.Feature).IsNull().Because("with no feature named, the requirement is named after the member");
    await Assert.That(RequiresPostgresExtensionAttribute.PROVIDER).IsEqualTo("postgres");
  }

  [Test]
  public async Task Requirement_ReadsAsItsCapabilityKeyAndValueAsync() {
    var onValue = new CapabilityRequirement(new CapabilityId("postgres", "pg_squeeze"), "Online table rewrite", "Whizbang:Workers:Maintenance:TableRewriteStrategy", "PgSqueeze");
    var onSet = new CapabilityRequirement(new CapabilityId("postgres", "pg_stat_statements"), "Advice", "Whizbang:Advisor:ReadStatements", null);

    await Assert.That(onValue.ToString()).IsEqualTo("postgres/pg_squeeze: Online table rewrite (Whizbang:Workers:Maintenance:TableRewriteStrategy = PgSqueeze)");
    await Assert.That(onSet.ToString()).IsEqualTo("postgres/pg_stat_statements: Advice (Whizbang:Advisor:ReadStatements)");
  }
}
