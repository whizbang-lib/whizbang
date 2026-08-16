#pragma warning disable CA1707

using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lineage;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// The FastEndpoints flavor of the apply-stack surface. The projection lives in
/// <see cref="ApplyStackReporter"/> and is covered exhaustively at the Core and ASP.NET surfaces;
/// these tests hold the FastEndpoints bases to their own contract — the consumer's endpoint
/// declares route and security, the base answers with the shared report, filters pass through.
/// </summary>
[Category("FastEndpoints")]
public class WhizbangApplyStackEndpointBaseTests {

  private sealed class SignaturesEndpoint : WhizbangApplyStackEndpointBase {
    public override void Configure() {
      Get("/whizbang/apply-stacks");
      AllowAnonymous();
    }
    public Task<ApplyStackReport> BuildForTestAsync(ApplyStackApiRequest req, CancellationToken ct) =>
      BuildReportAsync(req, ct);
  }

  private sealed class StreamsEndpoint : WhizbangApplyStackStreamsEndpointBase {
    public override void Configure() {
      Get("/whizbang/apply-stacks/streams");
      AllowAnonymous();
    }
    public Task<ApplyStackStreamsReport> BuildForTestAsync(ApplyStackStreamsApiRequest req, CancellationToken ct) =>
      BuildReportAsync(req, ct);
  }

  private sealed class FixedQuery : IApplyStackQuery {
    public ApplyStackQueryOptions? SeenOptions { get; private set; }
    public IReadOnlyList<string>? SeenPath { get; private set; }

    public Task<IReadOnlyList<ApplyPathSignature>> GetPathSignaturesAsync(
        ApplyStackQueryOptions options, CancellationToken cancellationToken = default) {
      SeenOptions = options;
      return Task.FromResult<IReadOnlyList<ApplyPathSignature>>(
        [new(["Created", "Closed"], 4, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]);
    }

    public Task<IReadOnlyList<Guid>> GetStreamsForPathAsync(
        IReadOnlyList<string> path, ApplyStackQueryOptions options, int limit,
        CancellationToken cancellationToken = default) {
      SeenPath = path;
      return Task.FromResult<IReadOnlyList<Guid>>([Guid.NewGuid()]);
    }
  }

  private static TEndpoint _endpointOver<TEndpoint>(IServiceProvider provider)
      where TEndpoint : class, IEndpoint {
    Factory.RegisterTestServices(_ => { });
    var httpContext = new DefaultHttpContext { RequestServices = provider };
    return Factory.Create<TEndpoint>(httpContext);
  }

  [Test]
  public async Task BuildReport_NoQueryRegistered_IsAStatedConditionAsync() {
    var services = new ServiceCollection();
    await using var provider = services.BuildServiceProvider();
    var endpoint = _endpointOver<SignaturesEndpoint>(provider);

    var report = await endpoint.BuildForTestAsync(new ApplyStackApiRequest(), CancellationToken.None);

    await Assert.That(report.Available).IsFalse()
      .Because("a host whose driver supplies no query gets a stated condition, never an empty list — "
             + "the FastEndpoints surface must hold the same line as the others");
    await Assert.That(report.Reason).Contains("IApplyStackQuery");
  }

  [Test]
  public async Task BuildReport_PassesFiltersThroughAndComputesTheFlowAsync() {
    var query = new FixedQuery();
    var services = new ServiceCollection();
    services.AddSingleton<IApplyStackQuery>(query);
    await using var provider = services.BuildServiceProvider();
    var endpoint = _endpointOver<SignaturesEndpoint>(provider);

    var report = await endpoint.BuildForTestAsync(
      new ApplyStackApiRequest { Perspective = "OrderList", Max = 25, Anchor = "Created", Radius = 1 },
      CancellationToken.None);

    await Assert.That(query.SeenOptions!.PerspectiveName).IsEqualTo("OrderList");
    await Assert.That(query.SeenOptions.MaxSignatures).IsEqualTo(25);
    await Assert.That(report.Flow).IsNotNull()
      .Because("an anchor was requested, so the shared projection computes the flow view");
    await Assert.That(report.Flow!.AnchorEventType).IsEqualTo("Created");
  }

  [Test]
  public async Task BuildStreamsReport_PassesTheExactPathThroughAsync() {
    var query = new FixedQuery();
    var services = new ServiceCollection();
    services.AddSingleton<IApplyStackQuery>(query);
    await using var provider = services.BuildServiceProvider();
    var endpoint = _endpointOver<StreamsEndpoint>(provider);

    var report = await endpoint.BuildForTestAsync(
      new ApplyStackStreamsApiRequest { Step = ["Created", "Updated+", "Closed"] },
      CancellationToken.None);

    await Assert.That(report.Available).IsTrue();
    await Assert.That(query.SeenPath!).IsEquivalentTo(["Created", "Updated+", "Closed"])
      .Because("the drill-in passes the exact collapsed path — the base adds nothing");
  }
}
