using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers <see cref="WhizbangAvailabilityStartupFilter"/>: it auto-injects the availability gate when
/// enabled AND a schema-ready gate is registered (MutationsOnly default → reads serve, writes 503 while
/// not ready), and skips itself when disabled or when there's no gate to gate on.
/// </summary>
public class WhizbangAvailabilityStartupFilterTests {

  private sealed class FakeGate : ISchemaReadyGate {
    public bool IsReady => false; // never ready, so the gate is active
    public void MarkReady() { }
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) =>
      Task.Delay(Timeout.Infinite, cancellationToken);
  }

  private static async Task<int> _statusAsync(WhizbangAvailabilityOptions options, bool gateRegistered, string method, string path) {
    var services = new ServiceCollection();
    services.AddSingleton<IOptions<WhizbangAvailabilityOptions>>(Options.Create(options));
    if (gateRegistered) {
      services.AddSingleton<ISchemaReadyGate>(new FakeGate());
    }
    using var provider = services.BuildServiceProvider();

    var app = new ApplicationBuilder(provider);
    var configure = new WhizbangAvailabilityStartupFilter().Configure(
      a => a.Run(ctx => { ctx.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; }));
    configure(app);
    var pipeline = app.Build();

    var context = new DefaultHttpContext { RequestServices = provider, Response = { Body = new MemoryStream() } };
    context.Request.Method = method;
    context.Request.Path = path;
    await pipeline(context);
    return context.Response.StatusCode;
  }

  [Test]
  public async Task Enabled_GatePresent_WriteIsGatedAsync()
    => await Assert.That(await _statusAsync(new WhizbangAvailabilityOptions(), gateRegistered: true, "POST", "/api/jobs"))
      .IsEqualTo(StatusCodes.Status503ServiceUnavailable);

  [Test]
  public async Task Enabled_GatePresent_ReadServesAsync()
    => await Assert.That(await _statusAsync(new WhizbangAvailabilityOptions(), gateRegistered: true, "GET", "/api/jobs"))
      .IsEqualTo(StatusCodes.Status200OK);

  [Test]
  public async Task Disabled_NotGatedAsync()
    => await Assert.That(await _statusAsync(new WhizbangAvailabilityOptions { Enabled = false }, gateRegistered: true, "POST", "/api/jobs"))
      .IsEqualTo(StatusCodes.Status200OK);

  [Test]
  public async Task NoGateRegistered_SkippedAsync()
    => await Assert.That(await _statusAsync(new WhizbangAvailabilityOptions(), gateRegistered: false, "POST", "/api/jobs"))
      .IsEqualTo(StatusCodes.Status200OK);
}
