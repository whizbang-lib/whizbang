// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;

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
    await Assert.That(() => GraphQLLensScopeResolver.ResolveForInput(GraphQLLensScopes.All, (WhizbangGraphQLOptions)null!))
        .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ResolveFromContext_WithOptionsPublished_UsesThemAsync() {
    var probe = new ScopeProbeType();
    _ = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d.Name("Query").Field("ping").Resolve("pong"))
        .AddType(probe)
        .AddWhizbangLenses(options => {
          options.DefaultScope = GraphQLLensScopes.All;
          options.IncludeScopeInFilters = false;
        })
        .BuildSchemaAsync();

    await Assert.That(probe.OutputScope).IsEqualTo(GraphQLLensScopes.All);
    await Assert.That(probe.InputScope).IsEqualTo(GraphQLLensScopes.Data | GraphQLLensScopes.Metadata | GraphQLLensScopes.SystemFields);
  }

  [Test]
  public async Task ResolveFromContext_WithForeignValueUnderTheOptionsKey_UsesTheDefaultsAsync() {
    // Something other than the options under the key is ignored: the defaults (data only) apply, never more.
    var probe = new ScopeProbeType();
    _ = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d.Name("Query").Field("ping").Resolve("pong"))
        .AddType(probe)
        .ConfigureSchema(builder => builder.SetContextData(GraphQLLensScopeResolver.OPTIONS_CONTEXT_KEY, "not options"))
        .BuildSchemaAsync();

    await Assert.That(probe.OutputScope).IsEqualTo(GraphQLLensScopes.Data);
    await Assert.That(probe.InputScope).IsEqualTo(GraphQLLensScopes.Data);
  }

  [Test]
  public async Task ResolveFromContext_WithNullContext_ThrowsAsync() {
    await Assert.That(() => GraphQLLensScopeResolver.ResolveForOutput(GraphQLLensScopes.All, null!))
        .Throws<ArgumentNullException>();
    await Assert.That(() => GraphQLLensScopeResolver.ResolveForInput(GraphQLLensScopes.All, (global::HotChocolate.Types.Descriptors.IDescriptorContext)null!))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Records what the context overloads resolve while the schema is built, the way generated lens types call them.
  /// </summary>
  public sealed class ScopeProbeType : ObjectType {
    public GraphQLLensScopes OutputScope { get; private set; }
    public GraphQLLensScopes InputScope { get; private set; }

    protected override void Configure(IObjectTypeDescriptor descriptor) {
      descriptor.Name("ScopeProbe");
      descriptor.Field("value").Resolve("v");
      var context = descriptor.Extend().Context;
      OutputScope = GraphQLLensScopeResolver.ResolveForOutput(GraphQLLensScopes.None, context);
      InputScope = GraphQLLensScopeResolver.ResolveForInput(GraphQLLensScopes.None, context);
    }
  }
}
