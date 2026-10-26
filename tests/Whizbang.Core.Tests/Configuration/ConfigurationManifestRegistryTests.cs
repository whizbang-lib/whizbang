// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Configuration;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// The process-wide registry each assembly's generated configuration manifest registers into at module load: the
/// sections it binds, the keys under each, and the capabilities its settings require. The capability verdict reads
/// the requirements from here, so it names features without reflecting over options types.
/// </summary>
/// <remarks>
/// The registry is static and shared with every other test in the process, and Whizbang.Core registers its own
/// manifest into it, so each test registers under an assembly name of its own and reads only what it registered.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Configuration/ConfigurationManifestRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Configuration/ConfigurationManifest.cs</code-under-test>
/// <docs>operations/infrastructure/capabilities#declaring-a-requirement</docs>
public sealed class ConfigurationManifestRegistryTests {
  private static readonly CapabilityRequirement _squeeze =
    new(new CapabilityId("postgres", "pg_squeeze"), "Online table rewrite", "Whizbang:Workers:Maintenance:TableRewriteStrategy", "PgSqueeze", "None");

  private static readonly CapabilityRequirement _partman =
    new(new CapabilityId("postgres", "pg_partman"), "Partition management", "Whizbang:Ledger:Partitioning:Managed", "true", "false");

  private static ConfigurationManifest _manifest(string assembly, params CapabilityRequirement[] requirements) =>
    new(assembly, [new ConfigurationBinding("Whizbang:Section", typeof(ConfigurationManifestRegistryTests), ["Whizbang:Section:Key"], requirements)]);

  [Test]
  public async Task Register_ListsTheManifestUnderItsAssemblyAsync() {
    var manifest = _manifest("Tests.Registry.Register", _squeeze);

    ConfigurationManifestRegistry.Register(manifest);

    await Assert.That(ConfigurationManifestRegistry.Manifests).Contains(manifest);
  }

  [Test]
  public async Task Register_SameAssemblyAgain_KeepsOnlyTheLatestAsync() {
    var first = _manifest("Tests.Registry.Replace", _squeeze);
    var second = _manifest("Tests.Registry.Replace", _partman);

    ConfigurationManifestRegistry.Register(first);
    ConfigurationManifestRegistry.Register(second);

    var registered = ConfigurationManifestRegistry.Manifests.Where(m => m.Assembly == "Tests.Registry.Replace").ToList();
    await Assert.That(registered).Count().IsEqualTo(1).Because("a module initializer that runs again replaces its own manifest");
    await Assert.That(registered[0]).IsSameReferenceAs(second);
  }

  [Test]
  public async Task Manifests_AreOrderedByAssemblyNameAsync() {
    ConfigurationManifestRegistry.Register(_manifest("Tests.Registry.Order.B"));
    ConfigurationManifestRegistry.Register(_manifest("Tests.Registry.Order.A"));

    var names = ConfigurationManifestRegistry.Manifests
      .Select(m => m.Assembly)
      .Where(name => name.StartsWith("Tests.Registry.Order.", StringComparison.Ordinal))
      .ToList();

    await Assert.That(names).IsEquivalentTo(["Tests.Registry.Order.A", "Tests.Registry.Order.B"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the order is the same on every start, whatever order the assemblies loaded in");
  }

  [Test]
  public async Task Requirements_AreEveryBindingsRequirementsOnceAcrossManifestsAsync() {
    var shared = new CapabilityRequirement(new CapabilityId("broker", "tests_shared"), "Shared feature", "Tests:Shared", null, null);
    ConfigurationManifestRegistry.Register(_manifest("Tests.Registry.Requirements.One", shared, _squeeze));
    ConfigurationManifestRegistry.Register(_manifest("Tests.Registry.Requirements.Two", shared));

    var requirements = ConfigurationManifestRegistry.Requirements;

    await Assert.That(requirements.Count(r => r == shared)).IsEqualTo(1)
      .Because("two assemblies binding the same setting state one requirement, not two");
    await Assert.That(requirements).Contains(_squeeze);
  }

  [Test]
  public async Task RemovedKeys_AreEveryManifestsDeclarations_FoundIgnoringCaseAsync() {
    var first = new RemovedConfigurationKeyEntry("Tests:Registry:Removed:Old", ["Tests:Registry:Removed:New"]);
    var second = new RemovedConfigurationKeyEntry("Tests:Registry:Removed:Other", []);
    ConfigurationManifestRegistry.Register(new ConfigurationManifest("Tests.Registry.Removed.One", []) { RemovedKeys = [first] });
    ConfigurationManifestRegistry.Register(new ConfigurationManifest("Tests.Registry.Removed.Two", []) { RemovedKeys = [second] });

    await Assert.That(ConfigurationManifestRegistry.RemovedKeys).Contains(first);
    await Assert.That(ConfigurationManifestRegistry.RemovedKeys).Contains(second);
    await Assert.That(ConfigurationManifestRegistry.FindRemovedKey("tests:registry:removed:old")).IsSameReferenceAs(first)
      .Because("configuration keys are case-insensitive, so a differently cased removed key is still the removed key");
    await Assert.That(ConfigurationManifestRegistry.FindRemovedKey("Tests:Registry:Removed:Never")).IsNull();
  }

  [Test]
  public async Task FindRemovedKey_DeclaredByTwoAssemblies_TheFirstByAssemblyNameWinsAsync() {
    var a = new RemovedConfigurationKeyEntry("Tests:Registry:RemovedTwice", ["Tests:Registry:FromA"]);
    var b = new RemovedConfigurationKeyEntry("Tests:Registry:RemovedTwice", ["Tests:Registry:FromB"]);
    ConfigurationManifestRegistry.Register(new ConfigurationManifest("Tests.Registry.RemovedTwice.B", []) { RemovedKeys = [b] });
    ConfigurationManifestRegistry.Register(new ConfigurationManifest("Tests.Registry.RemovedTwice.A", []) { RemovedKeys = [a] });

    await Assert.That(ConfigurationManifestRegistry.FindRemovedKey("Tests:Registry:RemovedTwice")).IsSameReferenceAs(a)
      .Because("the answer is the same on every start, whatever order the assemblies loaded in");
  }

  [Test]
  public async Task Manifest_WithNoRemovedKeysDeclared_HasNoneAsync() {
    await Assert.That(_manifest("Tests.Registry.NoRemoved").RemovedKeys).IsEmpty();
  }

  [Test]
  public async Task FindRemovedKey_Null_ThrowsAsync() {
    await Assert.That(() => ConfigurationManifestRegistry.FindRemovedKey(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Register_Null_ThrowsAsync() {
    await Assert.That(() => ConfigurationManifestRegistry.Register(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task WhizbangCore_RegistersItsOwnManifestAtLoadAsync() {
    var core = ConfigurationManifestRegistry.Manifests.Single(m => m.Assembly == "Whizbang.Core");
    var capabilities = core.Bindings.Single(b => b.Section == "Whizbang:Capabilities");

    await Assert.That(capabilities.OptionsType).IsEqualTo(typeof(CapabilityOptions));
    await Assert.That(capabilities.Keys).Contains("Whizbang:Capabilities:ProbeOnStartup")
      .Because("the generator runs over Whizbang.Core, so its own bindings are in its manifest without anyone listing them");
  }

  [Test]
  public async Task AttributedSetting_InThisAssembly_IsRegisteredAtLoadAsync() {
    var options = ManifestFixtureOptions.Bind(new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Tests:ManifestFixture:Search"] = "true" })
      .Build());

    var manifest = ConfigurationManifestRegistry.Manifests.Single(m => m.Assembly == "Whizbang.Core.Tests");

    await Assert.That(options.Search).IsTrue();
    await Assert.That(manifest.Bindings.Single(b => b.Section == "Tests:ManifestFixture").Requirements).Contains(
      new CapabilityRequirement(new CapabilityId("postgres", "pg_trgm"), "Fixture search", "Tests:ManifestFixture:Search", "true", "false"));
    await Assert.That(ConfigurationManifestRegistry.Requirements).Contains(
      new CapabilityRequirement(new CapabilityId("postgres", "pg_trgm"), "Fixture search", "Tests:ManifestFixture:Search", "true", "false"));
  }
}

/// <summary>An options type in this test assembly whose one setting requires a capability.</summary>
public sealed class ManifestFixtureOptions {
  /// <summary>Turns on a feature that needs trigram indexes.</summary>
  [RequiresPostgresExtension("pg_trgm", Feature = "Fixture search")]
  public bool Search { get; set; }

  /// <summary>Binds the options from <c>Tests:ManifestFixture</c>, a literal section the generator records.</summary>
  /// <param name="configuration">The configuration to read.</param>
  /// <returns>The bound options.</returns>
  public static ManifestFixtureOptions Bind(IConfiguration configuration) {
    var options = new ManifestFixtureOptions();
    configuration.GetSection("Tests:ManifestFixture").Bind(options);
    return options;
  }
}
