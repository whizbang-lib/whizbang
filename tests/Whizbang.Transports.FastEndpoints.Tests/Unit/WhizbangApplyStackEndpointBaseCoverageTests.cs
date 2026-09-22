#pragma warning disable CA1707

using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lineage;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// Coverage-round-23 targets: the <c>HandleAsync</c> overrides on
/// <see cref="WhizbangApplyStackEndpointBase"/> and <see cref="WhizbangApplyStackStreamsEndpointBase"/>.
/// </summary>
/// <remarks>
/// <see cref="WhizbangApplyStackEndpointBaseTests"/> only ever calls <c>BuildReportAsync</c>
/// directly, so it never exercises the two-line <c>HandleAsync</c> override that actually wires the
/// base into FastEndpoints: build the report, then hand it to <c>Send.OkAsync</c>. This file calls
/// <c>HandleAsync</c> itself and asserts on the endpoint's <c>Response</c> property (the DTO
/// <c>Send.OkAsync</c> hands to the serializer, per the FastEndpoints SDK docs) and on the
/// resulting HTTP status code.
/// </remarks>
/// <tests>src/Whizbang.Transports.FastEndpoints/Endpoints/WhizbangApplyStackEndpointBase.cs</tests>
public class WhizbangApplyStackEndpointBaseCoverageTests {

  private sealed class SignaturesEndpoint : WhizbangApplyStackEndpointBase {
    public override void Configure() {
      Get("/whizbang/apply-stacks");
      AllowAnonymous();
    }
  }

  private sealed class StreamsEndpoint : WhizbangApplyStackStreamsEndpointBase {
    public override void Configure() {
      Get("/whizbang/apply-stacks/streams");
      AllowAnonymous();
    }
  }

  private sealed class FixedQuery : IApplyStackQuery {
    public Task<IReadOnlyList<ApplyPathSignature>> GetPathSignaturesAsync(
        ApplyStackQueryOptions options, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<ApplyPathSignature>>(
        [new(["Created", "Closed"], 4, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]);

    public Task<IReadOnlyList<Guid>> GetStreamsForPathAsync(
        IReadOnlyList<string> path, ApplyStackQueryOptions options, int limit,
        CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<Guid>>([Guid.NewGuid()]);
  }

  private static TEndpoint _endpointOver<TEndpoint>(IServiceProvider provider)
      where TEndpoint : class, IEndpoint {
    Factory.RegisterTestServices(_ => { });
    var httpContext = new DefaultHttpContext { RequestServices = provider };
    return Factory.Create<TEndpoint>(httpContext);
  }

  // If HandleAsync stopped sending the built report to the caller -- a wrong status code, or a
  // report that never reaches Send -- every consumer's declared apply-stack endpoint would turn a
  // working projection into a client-visible error, or silently return nothing at all.
  [Test]
  public async Task HandleAsync_SendsTheBuiltReportWithOkAsync() {
    var query = new FixedQuery();
    var services = new ServiceCollection();
    services.AddSingleton<IApplyStackQuery>(query);
    await using var provider = services.BuildServiceProvider();
    var endpoint = _endpointOver<SignaturesEndpoint>(provider);

    await endpoint.HandleAsync(
      new ApplyStackApiRequest { Anchor = "Created", Radius = 1 },
      CancellationToken.None);

    await Assert.That(endpoint.HttpContext.Response.StatusCode).IsEqualTo(200)
      .Because("a wrong status code here turns a working projection into a client-visible error");
    await Assert.That(endpoint.Response).IsNotNull();
    await Assert.That(endpoint.Response.Available).IsTrue()
      .Because("HandleAsync must send the same report BuildReportAsync computed, not an empty stand-in");
    await Assert.That(endpoint.Response.Flow).IsNotNull()
      .Because("the anchor was supplied, so the flow view computed by BuildReportAsync must survive into what is sent");
  }

  // Same contract for the drill-in endpoint: if its HandleAsync regressed, "which streams took
  // this exact path" would silently stop reaching the caller even though the query behind it still
  // answers correctly -- an outage the caller could easily mistake for "no streams took this path".
  [Test]
  public async Task HandleAsync_StreamsEndpoint_SendsTheBuiltReportWithOkAsync() {
    var query = new FixedQuery();
    var services = new ServiceCollection();
    services.AddSingleton<IApplyStackQuery>(query);
    await using var provider = services.BuildServiceProvider();
    var endpoint = _endpointOver<StreamsEndpoint>(provider);

    await endpoint.HandleAsync(
      new ApplyStackStreamsApiRequest { Step = ["Created", "Closed"] },
      CancellationToken.None);

    await Assert.That(endpoint.HttpContext.Response.StatusCode).IsEqualTo(200)
      .Because("a wrong status code here turns a working drill-in into a client-visible error");
    await Assert.That(endpoint.Response).IsNotNull();
    await Assert.That(endpoint.Response.Available).IsTrue()
      .Because("HandleAsync must send the same report BuildReportAsync computed");
    await Assert.That(endpoint.Response.Streams).IsNotNull().Because("the fixed query always answers with a stream id");
  }
}
