using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Transports.HotChocolate.Middleware;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Coverage-round-23 targets for <see cref="OrderByStrippingMiddleware"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OrderByStrippingMiddlewareTests"/> only ever queries fields that carry a real "order"
/// argument (via <c>[UseSorting]</c>) backed by a resolver that always returns a genuine,
/// already-queryable source. That leaves four branches inside
/// <see cref="OrderByStrippingMiddleware"/> dark: a field with no "order" argument on the schema at
/// all (the catch path), a resolver whose result is null, a resolver whose result is not an
/// IQueryable, and an IQueryable with no pre-existing OrderBy for the visitor to strip.
/// </para>
/// <para>
/// This file wires its own tiny schema -- separate from the shared
/// <c>Whizbang.Transports.HotChocolate.Tests.Fixtures.GraphQLTestServer</c> fixture -- whose fields
/// are built to land on exactly those branches, without touching that shared fixture.
/// </para>
/// </remarks>
/// <tests>src/Whizbang.Transports.HotChocolate/Middleware/OrderByStrippingMiddleware.cs</tests>
public class OrderByStrippingMiddlewareCoverageTests {

  private static async Task<IRequestExecutor> _createExecutorAsync() {
    var services = new ServiceCollection();
    services.AddGraphQLServer().AddQueryType<CoverageQuery>();
    var provider = services.BuildServiceProvider();
    return await provider.GetRequestExecutorAsync();
  }

  // If the try/catch around the "order" lookup regressed, every field that opts into stripping
  // without also opting into GraphQL sorting (no [UseSorting], hence no "order" argument on the
  // schema) would fail its entire query with an unhandled exception instead of leaving the field's
  // own ordering alone.
  [Test]
  public async Task Middleware_FieldWithNoOrderArgumentOnTheSchema_SwallowsTheLookupAndLeavesOrderingAloneAsync() {
    var executor = await _createExecutorAsync();

    var result = await executor.ExecuteAsync("{ noOrderArgument { name } }");
    var json = result.ToJson(withIndentations: false);

    await Assert.That(json).DoesNotContain("errors")
      .Because("ArgumentValue(\"order\") throwing because the schema has no such argument must be "
             + "caught inside the middleware, never surfaced as a field error");
    var gammaIndex = json.IndexOf("Gamma", StringComparison.Ordinal);
    var betaIndex = json.IndexOf("Beta", StringComparison.Ordinal);
    var alphaIndex = json.IndexOf("Alpha", StringComparison.Ordinal);
    await Assert.That(gammaIndex).IsLessThan(betaIndex)
      .Because("with no sort argument to react to, the resolver's own descending order must survive untouched");
    await Assert.That(betaIndex).IsLessThan(alphaIndex);
  }

  // If the null-result guard regressed, a field whose resolver legitimately returns null would
  // throw while the middleware tried to inspect a nonexistent IQueryable, turning a normal null
  // result into an unhandled exception for the caller.
  [Test]
  public async Task Middleware_ResolverReturnsNull_LeavesTheNullResultAloneAsync() {
    var executor = await _createExecutorAsync();

    var result = await executor.ExecuteAsync("""{ nullResult(order: "asc") { name } }""");
    var json = result.ToJson(withIndentations: false);

    await Assert.That(json).DoesNotContain("errors")
      .Because("a null resolver result must pass straight through the middleware untouched");
    await Assert.That(json).Contains("\"nullResult\":null");
  }

  // If the IQueryable type check regressed, a field that opts into stripping but returns an
  // already-materialized collection (not an IQueryable) would throw an invalid-cast trying to
  // treat it as one, instead of leaving the collection exactly as the resolver produced it.
  [Test]
  public async Task Middleware_ResolverReturnsNonQueryable_LeavesTheCollectionAloneAsync() {
    var executor = await _createExecutorAsync();

    var result = await executor.ExecuteAsync("""{ nonQueryableResult(order: "asc") { name } }""");
    var json = result.ToJson(withIndentations: false);

    await Assert.That(json).DoesNotContain("errors")
      .Because("a resolver result that isn't IQueryable must be recognized and passed through, not crash the field");
    var alphaIndex = json.IndexOf("Alpha", StringComparison.Ordinal);
    var betaIndex = json.IndexOf("Beta", StringComparison.Ordinal);
    var gammaIndex = json.IndexOf("Gamma", StringComparison.Ordinal);
    await Assert.That(alphaIndex).IsLessThan(betaIndex)
      .Because("an already-materialized list has nothing for the middleware to strip, so its original order must survive");
    await Assert.That(betaIndex).IsLessThan(gammaIndex);
  }

  // If the reference-equality "nothing changed" check regressed, an IQueryable with no
  // pre-existing OrderBy would risk being needlessly rebuilt through a provider that may not
  // support re-running the plain expression, purely on a query no resolver ever ordered.
  [Test]
  public async Task Middleware_QueryableWithNoOrderByToStrip_LeavesTheQueryableAloneAsync() {
    var executor = await _createExecutorAsync();

    var result = await executor.ExecuteAsync("""{ unorderedQueryable(order: "asc") { name } }""");
    var json = result.ToJson(withIndentations: false);

    await Assert.That(json).DoesNotContain("errors")
      .Because("an IQueryable with no OrderBy to strip must still be returned to the caller");
    var alphaIndex = json.IndexOf("Alpha", StringComparison.Ordinal);
    var betaIndex = json.IndexOf("Beta", StringComparison.Ordinal);
    var gammaIndex = json.IndexOf("Gamma", StringComparison.Ordinal);
    await Assert.That(alphaIndex).IsLessThan(betaIndex)
      .Because("nothing here was ever ordered, so the source's own insertion order must be exactly what comes back");
    await Assert.That(betaIndex).IsLessThan(gammaIndex);
  }
}

/// <summary>
/// A minimal record used only by <see cref="OrderByStrippingMiddlewareCoverageTests"/>'s
/// purpose-built schema -- kept separate from the shared test fixtures.
/// </summary>
public sealed record CoverageWidget(string Name);

/// <summary>
/// Purpose-built query type, isolated from the shared
/// <c>Whizbang.Transports.HotChocolate.Tests.Fixtures.GraphQLTestServer</c> fixture, whose fields
/// are constructed to land on the branches of <see cref="OrderByStrippingMiddleware"/> that no
/// existing test reaches.
/// </summary>
public sealed class CoverageQuery {
  private static IQueryable<CoverageWidget> _widgets() =>
      new List<CoverageWidget> { new("Alpha"), new("Beta"), new("Gamma") }.AsQueryable();

  /// <summary>No [UseSorting], so the schema never gets an "order" argument for this field.</summary>
  [UseOrderByStripping]
  public IQueryable<CoverageWidget> GetNoOrderArgument() => _widgets().OrderByDescending(w => w.Name);

  /// <summary>Has an "order" argument (a plain string, not a real sort input) but resolves to null.</summary>
  [UseOrderByStripping]
  public CoverageWidget? GetNullResult(string? order) => null;

  /// <summary>Has an "order" argument but hands back an already-materialized, non-queryable list.</summary>
  [UseOrderByStripping]
  public IReadOnlyList<CoverageWidget> GetNonQueryableResult(string? order) => _widgets().ToList();

  /// <summary>Has an "order" argument and returns an IQueryable with no OrderBy call to strip.</summary>
  [UseOrderByStripping]
  public IQueryable<CoverageWidget> GetUnorderedQueryable(string? order) => _widgets();
}
