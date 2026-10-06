// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Loader;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using HotChocolate.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Transports.HotChocolate;
using Whizbang.Transports.HotChocolate.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// End-to-end tests for the field set a [GraphQLLens] exposes. Each test runs the generator over a
/// lens declaration, compiles the generated code against the real HotChocolate and Whizbang
/// assemblies, builds a schema from it and reads the exposed field set back out of that schema.
/// A string match on the generated source cannot tell whether a field reaches the schema; the built
/// schema can.
/// </summary>
/// <tests>Whizbang.Transports.HotChocolate.Generators/GraphQLLensTypeGenerator.cs</tests>
[Category("Generators")]
[Category("GraphQL")]
public class GraphQLLensScopeSchemaTests {
  private static readonly string[] _allRowFields = ["createdAt", "data", "id", "metadata", "scope", "updatedAt", "version"];
  private static readonly string[] _dataOnlyFields = ["data"];
  private static readonly string[] _noDataFields = ["createdAt", "id", "metadata", "scope", "updatedAt", "version"];
  private static readonly string[] _dataAndSystemFields = ["createdAt", "data", "id", "updatedAt", "version"];
  private static readonly string[] _dataAndMetadataFields = ["data", "metadata"];

  [Test]
  [RequiresAssemblyFiles]
  public async Task DataOnlyLens_ExposesOnlyDataAsync() {
    var schema = await _buildSchemaAsync("""[GraphQLLens(QueryName = "items", Scope = GraphQLLensScopes.DataOnly)]""");

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
    await Assert.That(_filterFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
    await Assert.That(_sortFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task NoDataLens_ExposesEverythingButDataAsync() {
    var schema = await _buildSchemaAsync("""[GraphQLLens(QueryName = "items", Scope = GraphQLLensScopes.NoData)]""");

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_noDataFields);
    await Assert.That(_filterFieldNames(schema, "items")).IsEquivalentTo(_noDataFields);
    await Assert.That(_sortFieldNames(schema, "items")).IsEquivalentTo(_noDataFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AllLens_ExposesEveryRowPartAsync() {
    var schema = await _buildSchemaAsync("""[GraphQLLens(QueryName = "items", Scope = GraphQLLensScopes.All)]""");

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_allRowFields);
    await Assert.That(_filterFieldNames(schema, "items")).IsEquivalentTo(_allRowFields);
    await Assert.That(_sortFieldNames(schema, "items")).IsEquivalentTo(_allRowFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task CombinedFlagsLens_ExposesExactlyTheCombinedPartsAsync() {
    var schema = await _buildSchemaAsync(
        """[GraphQLLens(QueryName = "items", Scope = GraphQLLensScopes.Data | GraphQLLensScopes.SystemFields)]""");

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_dataAndSystemFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnsetScope_WithDefaultOptions_FallsBackToDataOnlyAsync() {
    var schema = await _buildSchemaAsync("""[GraphQLLens(QueryName = "items")]""");

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
    await Assert.That(_filterFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
    await Assert.That(_sortFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnsetScope_HonorsANonDefaultDefaultScopeAsync() {
    var schema = await _buildSchemaAsync(
        """[GraphQLLens(QueryName = "items", Scope = GraphQLLensScopes.None)]""",
        options => options.DefaultScope = GraphQLLensScopes.Data | GraphQLLensScopes.Metadata);

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_dataAndMetadataFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ExplicitScope_IsNotOverriddenByDefaultScopeAsync() {
    var schema = await _buildSchemaAsync(
        """[GraphQLLens(QueryName = "items", Scope = GraphQLLensScopes.DataOnly)]""",
        options => options.DefaultScope = GraphQLLensScopes.All);

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnsetScope_WithDefaultScopeNone_FailsClosedToDataAsync() {
    var schema = await _buildSchemaAsync(
        """[GraphQLLens(QueryName = "items")]""",
        options => options.DefaultScope = GraphQLLensScopes.None);

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnsetScope_WithUnknownDefaultScope_FailsClosedToDataAsync() {
    var schema = await _buildSchemaAsync(
        """[GraphQLLens(QueryName = "items")]""",
        options => options.DefaultScope = GraphQLLensScopes.All | (GraphQLLensScopes)16);

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnknownDeclaredScope_FailsClosedToDataAsync() {
    // All four known parts plus an undefined bit: an unrecognized value never widens what is exposed.
    var schema = await _buildSchemaAsync("""[GraphQLLens(QueryName = "items", Scope = (GraphQLLensScopes)31)]""");

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnsetScope_WithoutWhizbangOptionsRegistered_FallsBackToDataOnlyAsync() {
    var schema = await _buildSchemaAsync("""[GraphQLLens(QueryName = "items")]""", configure: null, registerWhizbangLenses: false);

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task FilterAndSortExclusionOptions_NarrowOnlyTheInputTypesAsync() {
    var schema = await _buildSchemaAsync(
        """[GraphQLLens(QueryName = "items", Scope = GraphQLLensScopes.All)]""",
        options => {
          options.IncludeMetadataInFilters = false;
          options.IncludeScopeInFilters = false;
        });

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_allRowFields);
    await Assert.That(_filterFieldNames(schema, "items")).IsEquivalentTo(_dataAndSystemFields);
    await Assert.That(_sortFieldNames(schema, "items")).IsEquivalentTo(_dataAndSystemFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnpagedLens_ListElementTypeIsScopedAsync() {
    var schema = await _buildSchemaAsync(
        """[GraphQLLens(QueryName = "items", Scope = GraphQLLensScopes.DataOnly, EnablePaging = false, EnableFiltering = false, EnableSorting = false)]""");

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
    await Assert.That(schema.QueryType.Fields["items"].Type.IsListType()).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TwoLensesOverOneModel_EachKeepsItsOwnScopeAsync() {
    var schema = await _buildSchemaAsync(
        """
        [GraphQLLens(QueryName = "items", Scope = GraphQLLensScopes.DataOnly)]
        public interface IItemLens : ILensQuery<ItemModel> { }

        [GraphQLLens(QueryName = "adminItems", Scope = GraphQLLensScopes.All)]
        public interface IAdminItemLens : ILensQuery<ItemModel> { }
        """,
        configure: _ => { },
        registerWhizbangLenses: true,
        declarationIsComplete: true);

    await Assert.That(_rowFieldNames(schema, "items")).IsEquivalentTo(_dataOnlyFields);
    await Assert.That(_rowFieldNames(schema, "adminItems")).IsEquivalentTo(_allRowFields);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task DataOnlyLens_SchemaHasNoUnscopedRowTypeAsync() {
    var schema = await _buildSchemaAsync("""[GraphQLLens(QueryName = "items", Scope = GraphQLLensScopes.DataOnly)]""");

    // No output type anywhere in the schema may carry the row's tenancy scope for a data-only lens.
    var typesWithScopeField = schema.Types
        .OfType<ObjectType>()
        .Where(t => !t.Name.StartsWith("__", StringComparison.Ordinal) && t.Fields.ContainsField("scope") && t.Fields.ContainsField("data"))
        .Select(t => t.Name)
        .ToArray();
    await Assert.That(typesWithScopeField).IsEmpty();
  }

  // --- Harness --------------------------------------------------------------

  [RequiresAssemblyFiles]
  private static async Task<ISchema> _buildSchemaAsync(
      string lensDeclaration,
      Action<WhizbangGraphQLOptions>? configure = null,
      bool registerWhizbangLenses = true,
      bool declarationIsComplete = false) {
    var declarations = declarationIsComplete
        ? lensDeclaration
        : lensDeclaration + "\npublic interface IItemLens : ILensQuery<ItemModel> { }";
    var source = $$"""
        using System;
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.HotChocolate;

        namespace ScopeApp;

        public class Query {
          public string Ping => "pong";
        }

        public sealed class ItemModel {
          public string Name { get; set; } = "";
        }

        {{declarations}}
        """;

    var assembly = _compileWithGenerator(source);
    var queryClrType = assembly.GetType("ScopeApp.Query", throwOnError: true)!;
    var registration = assembly
        .GetType("ScopeApp.Generated.WhizbangLensRegistrationExtensions", throwOnError: true)!
        .GetMethod("AddWhizbangLensQueries")!;

    var builder = new ServiceCollection().AddGraphQL().AddQueryType(queryClrType);
    builder = registerWhizbangLenses
        ? builder.AddWhizbangLenses(configure ?? (_ => { }))
        : builder.AddFiltering().AddSorting().AddProjections();
    registration.Invoke(null, [builder]);

    return await builder.BuildSchemaAsync();
  }

  [RequiresAssemblyFiles]
  private static System.Reflection.Assembly _compileWithGenerator(string source) {
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(path => !System.IO.Path.GetFileName(path).Contains("Generators", StringComparison.Ordinal))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToList();

    var compilation = CSharpCompilation.Create(
        assemblyName: "ScopeApp_" + Guid.NewGuid().ToString("N"),
        syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    _ = CSharpGeneratorDriver.Create(new GraphQLLensTypeGenerator())
        .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

    using var stream = new MemoryStream();
    var emit = output.Emit(stream);
    if (!emit.Success) {
      var errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString());
      throw new InvalidOperationException("Generated lens code did not compile:\n" + string.Join("\n", errors));
    }

    stream.Position = 0;
    return AssemblyLoadContext.Default.LoadFromStream(stream);
  }

  private static ObjectType _rowType(ISchema schema, string queryField) {
    var named = schema.QueryType.Fields[queryField].Type.NamedType();
    if (named is ObjectType connection && connection.Fields.ContainsField("nodes")) {
      named = connection.Fields["nodes"].Type.NamedType();
    }
    return (ObjectType)named;
  }

  private static string[] _rowFieldNames(ISchema schema, string queryField) =>
      [.. _rowType(schema, queryField).Fields.Where(f => !f.IsIntrospectionField).Select(f => f.Name).Order(StringComparer.Ordinal)];

  private static string[] _filterFieldNames(ISchema schema, string queryField) =>
      _inputFieldNames(schema, queryField, "where", exclude: ["and", "or"]);

  private static string[] _sortFieldNames(ISchema schema, string queryField) =>
      _inputFieldNames(schema, queryField, "order", exclude: []);

  private static string[] _inputFieldNames(ISchema schema, string queryField, string argument, string[] exclude) {
    var input = (InputObjectType)schema.QueryType.Fields[queryField].Arguments[argument].Type.NamedType();
    return [.. input.Fields.Select(f => f.Name).Where(n => !exclude.Contains(n)).Order(StringComparer.Ordinal)];
  }
}
