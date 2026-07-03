using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Integration tests for the correlation seam through a REAL ASP.NET pipeline (TestServer): an inbound
/// <c>X-Correlation-ID</c> captured by the turnkey middleware (registered via <c>AddWhizbangAspNet</c>) must
/// still be visible when a downstream endpoint creates its dispatch root context
/// (<see cref="CascadeContext.NewRootWithAmbientSecurity"/>). This is the wiring the isolated unit tests did
/// not cover — each stubbed the stage before it.
/// </summary>
public class WhizbangCorrelationPipelineTests {

  private static IHostBuilder _hostCapturing(Action<CorrelationId?> capture) =>
    new HostBuilder().ConfigureWebHost(webBuilder => {
      webBuilder.UseTestServer();
      // Turnkey: this is the only registration a real service does.
      webBuilder.ConfigureServices(services => services.AddWhizbangAspNet());
      webBuilder.Configure(app => {
        // The endpoint stands in for a dispatch: it builds the root cascade context exactly as the
        // dispatcher does for a top-level send.
        app.Run(_ => {
          capture(CascadeContext.NewRootWithAmbientSecurity().CorrelationId);
          return Task.CompletedTask;
        });
      });
    });

  [Test]
  public async Task Correlation_FlowsThroughRealPipeline_ToDownstreamRootContextAsync() {
    InboundCorrelationAccessor.Current = null;
    var expected = CorrelationId.New(); // valid UUIDv7 the browser would send as a header
    CorrelationId? downstream = null;

    using var host = await _hostCapturing(c => downstream = c).StartAsync();
    var request = new HttpRequestMessage(HttpMethod.Get, "/");
    request.Headers.Add("X-Correlation-ID", expected.Value.ToString());

    await host.GetTestClient().SendAsync(request);

    await Assert.That(downstream).IsEqualTo(expected)
      .Because("The X-Correlation-ID captured by the turnkey middleware must reach the downstream dispatch's root context.");
  }

  [Test]
  public async Task Correlation_WithNoHeader_DownstreamGetsAFreshIdAsync() {
    InboundCorrelationAccessor.Current = null;
    CorrelationId? downstream = null;

    using var host = await _hostCapturing(c => downstream = c).StartAsync();
    await host.GetTestClient().GetAsync("/");

    await Assert.That(downstream).IsNotNull()
      .Because("With no inbound header the downstream context still mints a correlation id.");
    // v7 minted (no Activity in the test) — not adopted from a header.
    await Assert.That(downstream!.Value.Value.Version).IsEqualTo(7);
  }

  [Test]
  public async Task Correlation_IsNotSharedAcrossRequestsAsync() {
    // Two requests with different correlation headers must not bleed into each other (AsyncLocal isolation).
    InboundCorrelationAccessor.Current = null;
    var a = CorrelationId.New();
    var b = CorrelationId.New();
    CorrelationId? seenA = null, seenB = null;

    using var hostA = await _hostCapturing(c => seenA = c).StartAsync();
    var reqA = new HttpRequestMessage(HttpMethod.Get, "/");
    reqA.Headers.Add("X-Correlation-ID", a.Value.ToString());
    await hostA.GetTestClient().SendAsync(reqA);

    using var hostB = await _hostCapturing(c => seenB = c).StartAsync();
    var reqB = new HttpRequestMessage(HttpMethod.Get, "/");
    reqB.Headers.Add("X-Correlation-ID", b.Value.ToString());
    await hostB.GetTestClient().SendAsync(reqB);

    await Assert.That(seenA).IsEqualTo(a);
    await Assert.That(seenB).IsEqualTo(b);
  }
}
