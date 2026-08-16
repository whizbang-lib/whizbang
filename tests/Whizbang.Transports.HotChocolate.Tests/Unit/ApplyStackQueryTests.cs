#pragma warning disable CA1707

using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lineage;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// The <c>whizbangApplyStacks</c> / <c>whizbangApplyStackStreams</c> query fields — the GraphQL
/// flavor of the apply-stack surface. The projection is shared through
/// <see cref="ApplyStackReporter"/>; these tests hold the fields to their contract: explicitly
/// contributed (opt-in), same report shapes, honest degradation without a driver query, filters
/// and drill-in paths passing through unchanged.
/// </summary>
[Category("HotChocolate")]
public class ApplyStackQueryTests {

  private sealed class FixedQuery : IApplyStackQuery {
    public ApplyStackQueryOptions? SeenOptions { get; private set; }
    public IReadOnlyList<string>? SeenPath { get; private set; }

    public Task<IReadOnlyList<ApplyPathSignature>> GetPathSignaturesAsync(
        ApplyStackQueryOptions options, CancellationToken cancellationToken = default) {
      SeenOptions = options;
      return Task.FromResult<IReadOnlyList<ApplyPathSignature>>(
        [new(["Created", "Updated+", "Closed"], 7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]);
    }

    public Task<IReadOnlyList<Guid>> GetStreamsForPathAsync(
        IReadOnlyList<string> path, ApplyStackQueryOptions options, int limit,
        CancellationToken cancellationToken = default) {
      SeenPath = path;
      return Task.FromResult<IReadOnlyList<Guid>>([Guid.Parse("00000000-0000-0000-0000-000000000042")]);
    }
  }

  [Test]
  public async Task WhizbangApplyStacks_NoQueryRegistered_IsAStatedConditionAsync() {
    var services = new ServiceCollection();
    var result = await services
      .AddGraphQL()
      .AddQueryType(d => d.Name("Query"))
      .AddWhizbangApplyStacks()
      .ExecuteRequestAsync("{ whizbangApplyStacks { available reason } }");

    var json = result.ToJson();
    await Assert.That(json).Contains("\"available\": false")
      .Because("a host whose driver supplies no query gets a stated condition — the GraphQL surface holds the same line as the others");
    await Assert.That(json).Contains("IApplyStackQuery");
  }

  [Test]
  public async Task WhizbangApplyStacks_ServesSignaturesAndTheAnchoredFlowAsync() {
    var query = new FixedQuery();
    var services = new ServiceCollection();
    services.AddSingleton<IApplyStackQuery>(query);
    var result = await services
      .AddGraphQL()
      .AddQueryType(d => d.Name("Query"))
      .AddWhizbangApplyStacks()
      .ExecuteRequestAsync("""
        {
          whizbangApplyStacks(perspective: "OrderList", max: 25, anchor: "Updated", radius: 1) {
            available
            signatures { path streamCount }
            flow { anchorEventType nodes { offset eventType streamCount } }
          }
        }
        """);

    var json = result.ToJson();
    await Assert.That(json).Contains("\"available\": true");
    await Assert.That(json).Contains("Updated+");
    await Assert.That(json).Contains("\"anchorEventType\": \"Updated\"")
      .Because("an anchor argument computes the same flow view every other surface serves");
    await Assert.That(query.SeenOptions!.PerspectiveName).IsEqualTo("OrderList");
    await Assert.That(query.SeenOptions.MaxSignatures).IsEqualTo(25);
  }

  [Test]
  public async Task WhizbangApplyStackStreams_PassesTheExactPathThroughAsync() {
    var query = new FixedQuery();
    var services = new ServiceCollection();
    services.AddSingleton<IApplyStackQuery>(query);
    var result = await services
      .AddGraphQL()
      .AddQueryType(d => d.Name("Query"))
      .AddWhizbangApplyStacks()
      .ExecuteRequestAsync("""
        {
          whizbangApplyStackStreams(steps: ["Created", "Updated+", "Closed"]) {
            available
            streams
          }
        }
        """);

    var json = result.ToJson();
    await Assert.That(json).Contains("\"available\": true");
    await Assert.That(json).Contains("00000000-0000-0000-0000-000000000042");
    await Assert.That(query.SeenPath!).IsEquivalentTo(["Created", "Updated+", "Closed"])
      .Because("the drill-in passes the exact collapsed path — the field adds nothing");
  }
}
