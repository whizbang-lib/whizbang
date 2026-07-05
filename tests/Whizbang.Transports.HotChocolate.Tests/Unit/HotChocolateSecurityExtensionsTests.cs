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
        });

    if (preEnableExceptionDetails) {
      // Simulates a host that (like a dev-oriented default) turned details on earlier in the chain;
      // production hardening registered afterwards must win.
      builder = builder.ModifyRequestOptions(o => o.IncludeExceptionDetails = true);
    }

    if (isProduction is not null) {
      builder = builder.AddWhizbangGraphQLSecurityDefaults(isProduction.Value);
    }

    return services.BuildServiceProvider();
  }

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
}
