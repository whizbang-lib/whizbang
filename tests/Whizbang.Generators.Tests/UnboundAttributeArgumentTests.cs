// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;
using JsonIndexDiscovery = shared::Whizbang.Generators.Shared.Models.JsonIndexDiscovery;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Generators run on source that does not compile, which is what an IDE hands them mid-edit. An attribute
/// argument that does not bind (the wrong type, or a constructor no argument list matches) is read as
/// if it were not written: the output is exactly the output without it, never a guess and never a crash.
/// </summary>
/// <tests>src/Whizbang.Generators/PerspectiveSchemaGenerator.cs</tests>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCorePerspectiveConfigurationGenerator.cs</tests>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</tests>
/// <tests>src/Whizbang.Generators.Shared/Models/JsonIndexDiscovery.cs</tests>
[Category("SourceGenerators")]
public class UnboundAttributeArgumentTests {
  private static string _model(string modelAttribute, string fieldAttributes) => $$"""
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    {{modelAttribute}}
    public class CatalogModel {
      [StreamId]
      public Guid Id { get; set; }

      {{fieldAttributes}}
      public string Sku { get; set; } = "";
    }

    public record CatalogChanged : IEvent;

    public class CatalogPerspective : IPerspectiveFor<CatalogModel, CatalogChanged> {
      public CatalogModel Apply(CatalogModel currentData, CatalogChanged @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _allOutputAsync(string source) {
    var schema = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    var configuration = await GeneratorTestHelpers.RunEFCoreGeneratorWithEFCoreReferencesAsync(source);
    var registration = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    return string.Join("\n----\n",
      schema.GeneratedTrees.Select(t => t.ToString())
        .Concat(configuration.GeneratedSources.Select(s => s.SourceText.ToString()))
        .Concat(registration.GeneratedSources.Select(s => s.SourceText.ToString())));
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task PhysicalFieldUniqueThatDoesNotBind_GeneratesWhatOmittingItDoesAsync() {
    var split = "[PerspectiveStorage(FieldStorageMode.Split)]";
    var unbound = await _allOutputAsync(_model(split, "[PhysicalField(Unique = \"yes\")] [Indexed]"));
    var omitted = await _allOutputAsync(_model(split, "[PhysicalField] [Indexed]"));

    await Assert.That(unbound).Contains("sku")
      .Because("the setup must generate the field's column and index for the comparison to mean anything");
    await Assert.That(unbound).IsEqualTo(omitted)
      .Because("a Unique argument of the wrong type is not a request for uniqueness");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task PerspectiveStorageThatDoesNotBind_GeneratesWhatOmittingItDoesAsync() {
    var unbound = await _allOutputAsync(_model("[PerspectiveStorage(\"Split\")]", "[PhysicalField]"));
    var omitted = await _allOutputAsync(_model("", "[PhysicalField]"));

    await Assert.That(unbound).Contains("sku");
    await Assert.That(unbound).IsEqualTo(omitted)
      .Because("a storage mode the attribute cannot read is not Split");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task IndexedArgumentsThatDoNotBind_GenerateWhatABareIndexedDoesAsync() {
    var unbound = await _allOutputAsync(_model("", "[Indexed(\"ordered\", \"yes\")]"));
    var bare = await _allOutputAsync(_model("", "[Indexed]"));

    await Assert.That(bare).Contains("sku")
      .Because("the setup must generate the field's index for the comparison to mean anything");
    await Assert.That(unbound).IsEqualTo(bare)
      .Because("arguments no constructor accepts are neither a kind nor a request to fold case");
  }

  [Test]
  public async Task IndexedArgumentsThatDoNotBind_DeclareWhatABareIndexedDoesAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation("""
      using Whizbang.Core.Perspectives;

      namespace Probe;

      public class ProbeModel {
        [Indexed("ordered", "yes")]
        public string Unbound { get; init; } = "";

        [Indexed]
        public string Bare { get; init; } = "";
      }
      """);
    var model = compilation.GetTypeByMetadataName("Probe.ProbeModel")!;
    var unbound = model.GetMembers("Unbound").OfType<IPropertySymbol>().Single();
    var bare = model.GetMembers("Bare").OfType<IPropertySymbol>().Single();

    await Assert.That(JsonIndexDiscovery.DeclaredKind(unbound)).IsNotNull()
      .Because("the declaration is still an [Indexed], so it is read rather than skipped");
    await Assert.That(JsonIndexDiscovery.DeclaredKind(unbound)).IsEqualTo(JsonIndexDiscovery.DeclaredKind(bare))
      .Because("an argument list no constructor accepts falls back to the default kind");
    await Assert.That(JsonIndexDiscovery.DeclaresCaseInsensitive(unbound)).IsFalse()
      .Because("and to the default of respecting case");
    await Assert.That(JsonIndexDiscovery.DeclaresCaseInsensitive(bare)).IsFalse()
      .Because("which is also what a bound declaration's defaulted flag says");
    await Assert.That(JsonIndexDiscovery.DeclaredKind(unbound, caseInsensitive: false)).IsEqualTo(JsonIndexDiscovery.DeclaredKind(bare))
      .Because("so the declaration belongs to the plain expression");
  }

  [Test]
  public async Task CompositeIndexUniqueThatDoesNotBind_IsNotUniqueAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation("""
      using Whizbang.Core.Perspectives;

      namespace Probe;

      [PerspectiveIndex("Tenant", "Kind", Unique = "yes")]
      public class ProbeModel {
        public string Tenant { get; init; } = "";
        public string Kind { get; init; } = "";
      }
      """);

    var found = JsonIndexDiscovery.CompositesFrom(compilation.GetTypeByMetadataName("Probe.ProbeModel"));

    await Assert.That(found).Count().IsEqualTo(1);
    await Assert.That(found[0].Unique).IsFalse();
  }
}
