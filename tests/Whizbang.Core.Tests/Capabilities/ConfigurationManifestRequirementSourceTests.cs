// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Configuration;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// The first requirement source: every requirement the loaded assemblies' generated configuration manifests
/// declare. Later sources (vector columns found in the schema) add requirements the same way.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Capabilities/ICapabilityRequirementSource.cs</code-under-test>
/// <docs>operations/infrastructure/capabilities#requirement-sources</docs>
public class ConfigurationManifestRequirementSourceTests {
  [Test]
  public async Task Requirements_AreTheConfigurationManifestsAsync() {
    var declared = new CapabilityRequirement(new CapabilityId("broker", "source_test"), "Source test", "Tests:Source", null, "on");
    ConfigurationManifestRegistry.Register(new ConfigurationManifest("Tests.RequirementSource",
      [new ConfigurationBinding("Tests:Source", typeof(ConfigurationManifestRequirementSourceTests), ["Tests:Source"], [declared])]));

    var configured = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Tests:Source"] = "set" }).Build();

    await Assert.That(new ConfigurationManifestRequirementSource().Resolve(null)).Contains(
      new ActiveCapabilityRequirement(declared, "Tests:Source", "on", CapabilityActivation.Implicit))
      .Because("with no configuration, the declared default decides");
    await Assert.That(new ConfigurationManifestRequirementSource().Resolve(configured)).Contains(
      new ActiveCapabilityRequirement(declared, "Tests:Source", "set", CapabilityActivation.Explicit));
  }
}
