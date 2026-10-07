// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="GraphQLLensScopeResolver"/>: how a lens's declared scope and the system default
/// combine, and that every unrecognized or empty value fails closed to the data alone.
/// </summary>
public class GraphQLLensScopeResolverTests {
  [Test]
  [Arguments(GraphQLLensScopes.DataOnly, GraphQLLensScopes.All, GraphQLLensScopes.Data)]
  [Arguments(GraphQLLensScopes.NoData, GraphQLLensScopes.DataOnly, GraphQLLensScopes.NoData)]
  [Arguments(GraphQLLensScopes.All, GraphQLLensScopes.DataOnly, GraphQLLensScopes.All)]
  [Arguments(GraphQLLensScopes.None, GraphQLLensScopes.DataOnly, GraphQLLensScopes.Data)]
  [Arguments(GraphQLLensScopes.None, GraphQLLensScopes.NoData, GraphQLLensScopes.NoData)]
  [Arguments(GraphQLLensScopes.None, GraphQLLensScopes.None, GraphQLLensScopes.Data)]
  [Arguments(GraphQLLensScopes.None, (GraphQLLensScopes)16, GraphQLLensScopes.Data)]
  [Arguments((GraphQLLensScopes)31, GraphQLLensScopes.DataOnly, GraphQLLensScopes.Data)]
  [Arguments((GraphQLLensScopes)(-1), GraphQLLensScopes.All, GraphQLLensScopes.Data)]
  public async Task Resolve_ReturnsTheDeclaredOrDefaultScope_AndFailsClosedAsync(
      GraphQLLensScopes declared, GraphQLLensScopes defaultScope, GraphQLLensScopes expected) {
    var resolved = GraphQLLensScopeResolver.Resolve(declared, defaultScope);

    await Assert.That(resolved).IsEqualTo(expected);
  }

  [Test]
  public async Task ResolveForInput_DropsExcludedPartsButNeverAddsAnyAsync() {
    var options = new WhizbangGraphQLOptions { IncludeMetadataInFilters = false, IncludeScopeInFilters = false };

    var forAll = GraphQLLensScopeResolver.ResolveForInput(GraphQLLensScopes.All, options);
    var forDataOnly = GraphQLLensScopeResolver.ResolveForInput(GraphQLLensScopes.None, new WhizbangGraphQLOptions());

    await Assert.That(forAll).IsEqualTo(GraphQLLensScopes.Data | GraphQLLensScopes.SystemFields);
    await Assert.That(forDataOnly).IsEqualTo(GraphQLLensScopes.Data);
  }

  [Test]
  public async Task ResolveForInput_WithNullOptions_ThrowsAsync() {
    await Assert.That(() => GraphQLLensScopeResolver.ResolveForInput(GraphQLLensScopes.All, null!))
        .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task RowType_WithForeignValueUnderTheOptionsKey_UsesTheDefaultsAsync() {
    // Something other than the options under the key is ignored: the defaults (data only) apply, never more.
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType<ScopeQuery>()
        .ConfigureSchema(builder => builder.SetContextData(GraphQLLensScopeResolver.OPTIONS_CONTEXT_KEY, "not options"))
        .BuildSchemaAsync();

    var rowType = schema.GetType<ObjectType>("ResolverTestRow");
    var fields = rowType.Fields.Where(f => !f.IsIntrospectionField).Select(f => f.Name).ToArray();
    await Assert.That(fields).IsEquivalentTo(["data"]);
  }

  public sealed class ResolverTestModel {
    public string Name { get; set; } = string.Empty;
  }

  public sealed class ResolverTestRowType : LensPerspectiveRowType<ResolverTestModel> {
    public ResolverTestRowType() : base("ResolverTestRow", GraphQLLensScopes.None) { }
  }

  public sealed class ScopeQuery {
    [GraphQLType(typeof(NonNullType<ListType<NonNullType<ResolverTestRowType>>>))]
    public IQueryable<PerspectiveRow<ResolverTestModel>> GetItems() =>
        Enumerable.Empty<PerspectiveRow<ResolverTestModel>>().AsQueryable();
  }
}
