#pragma warning disable CA1707

using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Startup;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// Coverage-round-23 target: the <c>HandleAsync</c> override on
/// <see cref="WhizbangStartupStatusEndpointBase"/>.
/// </summary>
/// <remarks>
/// <see cref="WhizbangStartupStatusEndpointBaseTests"/> only ever calls <c>BuildReportAsync</c>
/// directly through a test-only wrapper, so it never exercises the two-line <c>HandleAsync</c>
/// override that actually wires the base into FastEndpoints: build the report, then hand it to
/// <c>Send.OkAsync</c>. This file calls <c>HandleAsync</c> itself and asserts on the endpoint's
/// <c>Response</c> property and the resulting HTTP status code, mirroring
/// <c>WhizbangApplyStackEndpointBaseCoverageTests</c>' approach for the sibling endpoint base.
/// </remarks>
/// <tests>src/Whizbang.Transports.FastEndpoints/Endpoints/WhizbangStartupStatusEndpointBase.cs</tests>
public class WhizbangStartupStatusEndpointBaseCoverageTests {

  private sealed class ProbeEndpoint : WhizbangStartupStatusEndpointBase {
    public override void Configure() {
      Get("/whizbang/startup");
      AllowAnonymous();
    }
  }

  private static TEndpoint _endpointOver<TEndpoint>(IServiceProvider provider)
      where TEndpoint : class, IEndpoint {
    Factory.RegisterTestServices(_ => { });
    var httpContext = new DefaultHttpContext { RequestServices = provider };
    return Factory.Create<TEndpoint>(httpContext);
  }

  // A readiness/liveness probe reads this endpoint to decide whether to route traffic to this
  // instance. If HandleAsync stopped sending the built report -- a wrong status code, or a report
  // that never reaches Send -- a probe would either hold a healthy deployment out of rotation, or
  // (worse) keep routing to a host whose status check silently hangs or errors instead of
  // answering honestly.
  [Test]
  public async Task HandleAsync_SendsTheBuiltReportWithOkAsync() {
    var services = new ServiceCollection();  // nothing registered -- BuildReportAsync must degrade honestly
    await using var provider = services.BuildServiceProvider();
    var endpoint = _endpointOver<ProbeEndpoint>(provider);

    await endpoint.HandleAsync(CancellationToken.None);

    await Assert.That(endpoint.HttpContext.Response.StatusCode).IsEqualTo(200)
      .Because("a wrong status code here is exactly what turns a healthy probe target into a failed check");
    await Assert.That(endpoint.Response).IsNotNull();
    await Assert.That(endpoint.Response.Instance.Started).IsFalse()
      .Because("HandleAsync must send the same report BuildReportAsync computed -- no pipeline registered projects as 'not started'");
    await Assert.That(endpoint.Response.Fleet.Available).IsFalse()
      .Because("no fleet source registered is a stated condition that the sent report must carry through, not an empty fleet");
  }
}
