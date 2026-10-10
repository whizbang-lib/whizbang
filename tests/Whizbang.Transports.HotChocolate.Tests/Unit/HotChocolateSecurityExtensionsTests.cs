// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Transports.HotChocolate;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="HotChocolateSecurityExtensions"/>. Verifies that production mode rejects
/// introspection and strips exception details from errors, while non-production mode is a no-op that
/// preserves the full developer experience.
/// </summary>
public class HotChocolateSecurityExtensionsTests {

  // Builds a provider hosting a minimal schema: a working field and a field whose resolver throws,
  // so tests can observe both validation-time and execution-time error shaping.
  private static ServiceProvider _createProvider(
      bool? isProduction,
      bool preEnableExceptionDetails = false) {
    var services = new ServiceCollection();
    var builder = services
        .AddGraphQL()
        .AddQueryType(d => {
          d.Name("Query");
          d.Field("hello").Type<StringType>().Resolve("world");
          d.Field("boom").Type<StringType>().Resolve<string>(_ => _throwBoom());
          d.Field("node").Type<NodeType>().Resolve(new Node());
        })
        .AddType<NodeType>();

    if (preEnableExceptionDetails) {
      // Simulates a host that (like a dev-oriented default) turned details on earlier in the chain —
      // production hardening registered afterwards must win.
      builder = builder.ModifyRequestOptions(o => o.IncludeExceptionDetails = true);
    }

    if (isProduction is not null) {
      _ = builder.AddWhizbangGraphQLSecurityDefaults(isProduction.Value);
    }

    return services.BuildServiceProvider();
  }

  // A self-referencing type, so a query can be made as deep as a test needs.
  private sealed class Node {
    public string Name { get; } = "n";
  }

  private sealed class NodeType : ObjectType<Node> {
    protected override void Configure(IObjectTypeDescriptor<Node> descriptor) {
      descriptor.Name("Node");
      descriptor.Field(n => n.Name);
      descriptor.Field("child").Type<NodeType>().Resolve(new Node());
    }
  }

  // "{ node { child { child ... { name } } } }" with the given number of child levels.
  private static string _nestedQuery(int childLevels)
    => "{ node { " + string.Concat(Enumerable.Repeat("child { ", childLevels)) + "name"
      + string.Concat(Enumerable.Repeat(" }", childLevels)) + " } }";

  private static string _throwBoom()
    => throw new InvalidOperationException("secret-internal-detail");

  private static async Task<string> _executeToJsonAsync(ServiceProvider provider, string query) {
    var executor = await provider.GetRequestExecutorAsync();
    var result = await executor.ExecuteAsync(query);
    return result.ToJson();
  }

  [Test]
  public async Task AddWhizbangGraphQLSecurityDefaults_ReturnsBuilderForChainingAsync() {
    var builder = new ServiceCollection().AddGraphQL();

    var returned = builder.AddWhizbangGraphQLSecurityDefaults(isProduction: true);

    await Assert.That(returned).IsNotNull();
    await Assert.That(returned).IsSameReferenceAs(builder);
  }

  [Test]
  public async Task Production_RejectsIntrospectionQueryAsync() {
    await using var provider = _createProvider(isProduction: true);

    var json = await _executeToJsonAsync(provider, "{ __schema { queryType { name } } }");

    await Assert.That(json).Contains("Introspection is not allowed")
      .Because("Anonymous callers must not be able to enumerate the schema in production.");
  }

  [Test]
  public async Task NonProduction_AllowsIntrospectionQueryAsync() {
    await using var provider = _createProvider(isProduction: false);

    var json = await _executeToJsonAsync(provider, "{ __schema { queryType { name } } }");

    await Assert.That(json).Contains("queryType")
      .Because("Development tooling (Banana Cake Pop, IDE plugins) relies on introspection.");
    await Assert.That(json).DoesNotContain("Introspection is not allowed");
  }

  [Test]
  public async Task Production_StripsExceptionDetailsFromErrorsAsync() {
    await using var provider = _createProvider(isProduction: true, preEnableExceptionDetails: true);

    var json = await _executeToJsonAsync(provider, "{ boom }");

    await Assert.That(json).DoesNotContain("secret-internal-detail")
      .Because("Exception messages leak internal implementation detail through the errors payload.");
    await Assert.That(json).DoesNotContain("stackTrace");
  }

  [Test]
  public async Task NonProduction_IsNoOp_KeepsExceptionDetailsAsync() {
    await using var provider = _createProvider(isProduction: false, preEnableExceptionDetails: true);

    var json = await _executeToJsonAsync(provider, "{ boom }");

    await Assert.That(json).Contains("secret-internal-detail")
      .Because("Non-production mode must not degrade the local debugging experience.");
  }

  [Test]
  public async Task UnknownField_ErrorCarriesNoFieldNameSuggestionAsync() {
    // Regression pin: HotChocolate 15.x does not implement similar-name field suggestions for unknown
    // fields. If a future upgrade introduces "Did you mean ...?" hints, this test surfaces it so the
    // hardening extension can be extended before the leak reaches production.
    await using var provider = _createProvider(isProduction: true);

    var json = await _executeToJsonAsync(provider, "{ helo }");

    await Assert.That(json).Contains("does not exist")
      .Because("The unknown-field validation error itself is expected.");
    await Assert.That(json).DoesNotContain("Did you mean");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AQueryDeeperThanTheLimit_IsRejectedBeforeItRunsAsync(bool isProduction) {
    // Depth multiplies resolver work and is not bounded by HotChocolate's defaults. The limit applies in
    // every environment, so a query that development accepts is never one production refuses.
    await using var provider = _createProvider(isProduction);

    var json = await _executeToJsonAsync(provider,
      _nestedQuery(HotChocolateSecurityExtensions.DEFAULT_MAX_EXECUTION_DEPTH + 1));

    await Assert.That(json).Contains("depth")
      .Because("the validation error names the execution-depth rule");
    await Assert.That(json).DoesNotContain("\"name\"")
      .Because("a rejected query resolves nothing");
  }

  [Test]
  public async Task AQueryAtTheLimit_RunsAsync() {
    await using var provider = _createProvider(isProduction: true);

    // node is level 1 and name sits one level below the deepest child.
    var json = await _executeToJsonAsync(provider,
      _nestedQuery(HotChocolateSecurityExtensions.DEFAULT_MAX_EXECUTION_DEPTH - 2));

    await Assert.That(json).Contains("\"name\"").Because("the deepest field resolved");
    await Assert.That(json).DoesNotContain("errors");
  }

  [Test]
  public async Task TheDepthLimit_CanBeSetByTheHostAsync() {
    var services = new ServiceCollection();
    _ = services.AddGraphQL()
      .AddQueryType(d => {
        d.Name("Query");
        d.Field("node").Type<NodeType>().Resolve(new Node());
      })
      .AddType<NodeType>()
      .AddWhizbangGraphQLSecurityDefaults(isProduction: true, maxExecutionDepth: 3);
    await using var provider = services.BuildServiceProvider();

    var json = await _executeToJsonAsync(provider, _nestedQuery(3));

    await Assert.That(json).Contains("depth");
  }

  [Test]
  public async Task Introspection_IsNotCountedAgainstTheDepthLimitInDevelopmentAsync() {
    // A full introspection query nests ofType a dozen levels deep; development tooling sends one.
    await using var provider = _createProvider(isProduction: false);

    var json = await _executeToJsonAsync(provider,
      "{ __schema { types { fields { type { ofType { ofType { ofType { ofType { ofType { ofType { ofType { ofType { ofType { ofType { name } } } } } } } } } } } } } } }");

    await Assert.That(json).DoesNotContain("depth");
    await Assert.That(json).Contains("__schema");
  }
}
