using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Verifies the security-headers middleware is turnkey: <c>AddWhizbangAspNet</c> auto-injects it via
/// <see cref="WhizbangSecurityHeadersStartupFilter"/>, so responses carry hardened headers with no explicit
/// pipeline wiring — while method filtering stays off by default and can be opted into via options.
/// </summary>
public class WhizbangSecurityHeadersStartupFilterTests {

  private static async Task<IHost> _startHostAsync(Action<IServiceCollection>? extraServices = null) {
    return await new HostBuilder()
      .ConfigureWebHost(webBuilder => {
        webBuilder.UseTestServer();
        webBuilder.ConfigureServices(services => {
          services.AddWhizbangAspNet();
          extraServices?.Invoke(services);
        });
        // Note: NOT calling app.UseWhizbangSecurityHeaders() — the startup filter does it.
        webBuilder.Configure(app => app.Run(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }));
      })
      .StartAsync();
  }

  [Test]
  public async Task StartupFilter_AutoInjectsHardenedHeadersAsync() {
    using var host = await _startHostAsync();
    var client = host.GetTestClient();

    var response = await client.GetAsync("/anything");

    await Assert.That(response.Headers.GetValues("X-Content-Type-Options")).Contains("nosniff")
      .Because("AddWhizbangAspNet must apply security headers turnkey — no explicit pipeline call.");
    await Assert.That(response.Headers.GetValues("X-Frame-Options")).Contains("DENY");
  }

  [Test]
  public async Task StartupFilter_MethodFilteringOffByDefault_AllowsPutAsync() {
    using var host = await _startHostAsync();
    var client = host.GetTestClient();

    using var request = new HttpRequestMessage(HttpMethod.Put, "/anything");
    var response = await client.SendAsync(request);

    await Assert.That((int)response.StatusCode).IsEqualTo(200)
      .Because("Turnkey wiring must not 405 PUT/PATCH/DELETE — method filtering is opt-in.");
  }

  [Test]
  public async Task StartupFilter_MethodFilteringOptIn_Returns405Async() {
    using var host = await _startHostAsync(services =>
      services.Configure<WhizbangSecurityHeadersOptions>(o => {
        o.AllowedMethods.Add("GET");
        o.AllowedMethods.Add("HEAD");
      }));
    var client = host.GetTestClient();

    using var request = new HttpRequestMessage(HttpMethod.Put, "/anything");
    var response = await client.SendAsync(request);

    await Assert.That((int)response.StatusCode).IsEqualTo(405)
      .Because("A service that opts into an allowlist rejects verbs outside it.");
  }

  [Test]
  public async Task StartupFilter_Disabled_EmitsNoSecurityHeadersAsync() {
    using var host = await _startHostAsync(services =>
      services.Configure<WhizbangSecurityHeadersOptions>(o => o.Enabled = false));
    var client = host.GetTestClient();

    var response = await client.GetAsync("/anything");

    await Assert.That(response.Headers.Contains("X-Frame-Options")).IsFalse()
      .Because("Enabled = false is the turnkey opt-out.");
  }
}
