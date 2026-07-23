using Microsoft.AspNetCore.Http;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers <see cref="AvailabilityGateMode.MutationsOnly"/>: while the schema is not ready, safe reads
/// pass through and only mutating requests are 503'd — so a host serves reads (untouched read-model
/// tables) during a migration while the write path waits. The default mode stays all-non-exempt.
/// </summary>
public class DatabaseAvailabilityMiddlewareModeTests {

  private sealed class FakeGate(bool ready) : ISchemaReadyGate {
    public bool IsReady { get; } = ready;
    public void MarkReady() { }
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private static async Task<int> _statusAsync(AvailabilityGateMode mode, string method, string path, bool ready) {
    var middleware = new DatabaseAvailabilityMiddleware(
      next: ctx => { ctx.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; },
      schemaReadyGate: new FakeGate(ready), exemptPaths: null, mode: mode);
    var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
    context.Request.Method = method;
    context.Request.Path = path;
    await middleware.InvokeAsync(context);
    return context.Response.StatusCode;
  }

  [Test]
  public async Task MutationsOnly_NotReady_ReadPassesThroughAsync()
    => await Assert.That(await _statusAsync(AvailabilityGateMode.MutationsOnly, "GET", "/api/jobs", ready: false))
      .IsEqualTo(StatusCodes.Status200OK);

  [Test]
  public async Task MutationsOnly_NotReady_WriteIsGatedAsync()
    => await Assert.That(await _statusAsync(AvailabilityGateMode.MutationsOnly, "POST", "/api/jobs", ready: false))
      .IsEqualTo(StatusCodes.Status503ServiceUnavailable);

  [Test]
  public async Task MutationsOnly_ProbePathAlwaysExemptAsync()
    => await Assert.That(await _statusAsync(AvailabilityGateMode.MutationsOnly, "GET", "/alive", ready: false))
      .IsEqualTo(StatusCodes.Status200OK);

  [Test]
  public async Task AllNonExempt_NotReady_ReadIsGatedAsync() // default behavior unchanged
    => await Assert.That(await _statusAsync(AvailabilityGateMode.AllNonExempt, "GET", "/api/jobs", ready: false))
      .IsEqualTo(StatusCodes.Status503ServiceUnavailable);

  [Test]
  public async Task Ready_PassesThroughRegardlessOfMethodAsync()
    => await Assert.That(await _statusAsync(AvailabilityGateMode.MutationsOnly, "POST", "/api/jobs", ready: true))
      .IsEqualTo(StatusCodes.Status200OK);
}
