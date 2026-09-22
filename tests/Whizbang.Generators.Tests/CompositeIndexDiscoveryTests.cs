extern alias shared;

using System.Collections.Immutable;

using Microsoft.CodeAnalysis;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using CompositeIndexSql = shared::Whizbang.Generators.Shared.Models.CompositeIndexSql;
using JsonIndexDiscovery = shared::Whizbang.Generators.Shared.Models.JsonIndexDiscovery;

namespace Whizbang.Generators.Tests;

/// <summary>
/// What <c>[PerspectiveIndex]</c> discovery makes of each shape of declaration.
/// </summary>
/// <remarks>
/// The end-to-end case proves a well-formed declaration reaches the DDL. These cover the branches it
/// does not: the options read from named arguments, and the four ways a declaration resolves to
/// nothing. The last group matters most, because the safe answer there is to emit no index at all --
/// a composite missing one of its columns is a different index, and one that silently answered fewer
/// filters would be worse than none.
/// </remarks>
/// <code-under-test>src/Whizbang.Generators.Shared/Models/JsonIndexDiscovery.cs</code-under-test>
[Category("SourceGenerators")]
public class CompositeIndexDiscoveryTests {

  /// <summary>Compiles a model carrying <paramref name="declarations"/> and discovers its composites.</summary>
  private static ImmutableArray<shared::Whizbang.Generators.Shared.Models.CompositeIndexInfo> _discover(
      string declarations, string properties) {
    var source = $$"""
      using System;
      using Whizbang.Core.Perspectives;

      namespace Probe;

      {{declarations}}
      public class ProbeModel {
      {{properties}}
      }
      """;

    var compilation = GeneratorTestHelper.CreateCompilation(source);

    // Every case below asserts on an empty or single-element result, which is also what discovery
    // answers for a source that did not compile -- so an unbound attribute would pass all of them
    // while proving nothing. Fail on it here instead.
    var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length > 0) {
      throw new InvalidOperationException(
        $"the probe source did not compile: {string.Join("; ", errors.Select(e => e.ToString()))}");
    }

    var model = compilation.GetTypeByMetadataName("Probe.ProbeModel")!;

    return JsonIndexDiscovery.CompositesFrom(model);
  }

  [Test]
  public async Task ADeclaredNameAndUniqueness_AreReadFromTheDeclarationAsync() {
    var found = _discover(
      """[PerspectiveIndex("Tenant", "Kind", Name = "idx_chosen", Unique = true)]""",
      """
        public string Tenant { get; init; } = "";
        public string Kind { get; init; } = "";
      """);

    await Assert.That(found).Count().IsEqualTo(1);
    await Assert.That(found[0].Name).IsEqualTo("idx_chosen")
      .Because("a declared name has to survive discovery, or naming an index does nothing.");
    await Assert.That(found[0].Unique).IsTrue();
    await Assert.That(CompositeIndexSql.Name(found[0], "probe")).IsEqualTo("idx_chosen")
      .Because("and the declared name wins over the derived one.");
  }

  [Test]
  public async Task APromotedPropertysDeclaredColumnName_IsWhatTheIndexUsesAsync() {
    var found = _discover(
      """[PerspectiveIndex("Tenant")]""",
      """
        [PhysicalField(ColumnName = "tenant_key")]
        public Guid Tenant { get; init; }
      """);

    await Assert.That(found).Count().IsEqualTo(1);
    await Assert.That(found[0].Elements[0].Element).IsEqualTo("tenant_key")
      .Because("the index has to name the column the property was actually promoted to, not the one "
             + "the naming convention would have produced.");
  }

  /// <summary>A promoted property with no declared name falls to the convention.</summary>
  [Test]
  public async Task APromotedPropertyWithoutADeclaredName_UsesTheConventionAsync() {
    var found = _discover(
      """[PerspectiveIndex("TenantId")]""",
      """
        [PhysicalField]
        public Guid TenantId { get; init; }
      """);

    await Assert.That(found[0].Elements[0].Element).IsEqualTo("tenant_id");
  }

  /// <summary>A property the model does not have means no index.</summary>
  /// <remarks>
  /// Dropped whole rather than emitted without it. This is also the case that is not yet reported as
  /// a build diagnostic, which the attribute's documentation says plainly.
  /// </remarks>
  [Test]
  public async Task AnUnknownProperty_MeansNoIndexAsync() {
    var found = _discover(
      """[PerspectiveIndex("Tenant", "Misspelled")]""",
      """
        public string Tenant { get; init; } = "";
      """);

    await Assert.That(found).IsEmpty()
      .Because("a composite missing one of its columns is a different index, so emitting it partially "
             + "would answer fewer filters than declared while looking correct.");
  }

  /// <summary>A property whose type cannot carry an index means no index either.</summary>
  [Test]
  public async Task APropertyWhoseTypeCannotBeIndexed_MeansNoIndexAsync() {
    var found = _discover(
      """[PerspectiveIndex("Tenant", "Payload")]""",
      """
        public string Tenant { get; init; } = "";
        public object Payload { get; init; } = new();
      """);

    await Assert.That(found).IsEmpty()
      .Because("an index has to be built from an immutable expression, and a type with no cast has "
             + "none -- so there is nothing to build the element from.");
  }

  [Test]
  public async Task ADeclarationNamingNoProperties_MeansNoIndexAsync() {
    var found = _discover(
      """[PerspectiveIndex]""",
      """
        public string Tenant { get; init; } = "";
      """);

    await Assert.That(found).IsEmpty()
      .Because("an index over no properties is not an index; emitting one would be a syntax error in "
             + "the middle of the schema pass.");
  }

  [Test]
  public async Task AModelWithNoDeclarations_HasNoCompositesAsync() {
    var found = _discover(string.Empty, """
        public string Tenant { get; init; } = "";
      """);

    await Assert.That(found).IsEmpty();
  }

  /// <summary>
  /// A null model answers with nothing, which every one of these helpers has to do.
  /// </summary>
  /// <remarks>
  /// The semantic model returns null for a type it could not resolve, which happens for real:
  /// mid-edit source in the IDE, or a model referencing a type from an assembly that failed to load.
  /// </remarks>
  [Test]
  public async Task ANullModel_HasNoCompositesAsync() {
    await Assert.That(JsonIndexDiscovery.CompositesFrom(null)).IsEmpty();
  }

  /// <summary>And a null declaration has no name rather than throwing.</summary>
  [Test]
  public async Task ANullDeclaration_HasNoNameAsync() {
    await Assert.That(CompositeIndexSql.Name(null!, "probe")).IsEmpty()
      .Because("the renderer is reached from a generator, where throwing fails a whole compilation "
             + "rather than skipping one index.");
  }
}
