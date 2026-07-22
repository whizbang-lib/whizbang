using System.Text;
using Microsoft.AspNetCore.Http;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers DatabaseAvailabilityMiddleware's two branches: not-ready (returns 503
/// with retry-after) and ready (delegates to next). The class previously sat
/// at 0% line coverage in the unit suite because no fixture had constructed
/// a real HttpContext + ISchemaReadyGate combination.
/// </summary>
public class DatabaseAvailabilityMiddlewareTests {

  private sealed class FakeGate(bool ready = false) : ISchemaReadyGate {
    public bool IsReady { get; private set; } = ready;
    public void MarkReady() => IsReady = true;
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) {
      return IsReady ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);
    }
  }

  [Test]
  public async Task NotReady_Returns503AndRetryAfterAsync() {
    var nextCalled = false;
    var context = new DefaultHttpContext();
    context.Response.Body = new MemoryStream();

    var middleware = new DatabaseAvailabilityMiddleware(
      next: _ => { nextCalled = true; return Task.CompletedTask; },
      schemaReadyGate: new FakeGate(ready: false));

    await middleware.InvokeAsync(context);

    await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status503ServiceUnavailable);
    await Assert.That(context.Response.Headers.RetryAfter.ToString()).IsEqualTo("30");
    await Assert.That(context.Response.ContentType).IsEqualTo("application/json");
    await Assert.That(nextCalled).IsFalse();

    context.Response.Body.Position = 0;
    using var sr = new StreamReader(context.Response.Body, Encoding.UTF8);
    var body = await sr.ReadToEndAsync();
    await Assert.That(body).Contains("schema_initializing");
  }

  [Test]
  public async Task Ready_DelegatesToNextAsync() {
    var nextCalled = false;
    var context = new DefaultHttpContext();
    context.Response.Body = new MemoryStream();

    var middleware = new DatabaseAvailabilityMiddleware(
      next: _ => { nextCalled = true; return Task.CompletedTask; },
      schemaReadyGate: new FakeGate(ready: true));

    await middleware.InvokeAsync(context);

    await Assert.That(nextCalled).IsTrue();
    await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
  }

  [Test]
  public async Task NotReady_DefaultExemptPath_PassesThroughAsync() {
    var nextCalled = false;
    var context = new DefaultHttpContext();
    context.Request.Path = "/alive";
    context.Response.Body = new MemoryStream();

    var middleware = new DatabaseAvailabilityMiddleware(
      next: _ => { nextCalled = true; return Task.CompletedTask; },
      schemaReadyGate: new FakeGate(ready: false));

    await middleware.InvokeAsync(context);

    // /alive is a default exempt path: passes through even though the schema is not ready, so the
    // liveness probe keeps succeeding while migrations run.
    await Assert.That(nextCalled).IsTrue();
    await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
  }

  [Test]
  public async Task NotReady_CustomExemptPath_PassesThroughAsync() {
    var nextCalled = false;
    var context = new DefaultHttpContext();
    context.Request.Path = "/probe";
    context.Response.Body = new MemoryStream();

    var middleware = new DatabaseAvailabilityMiddleware(
      next: _ => { nextCalled = true; return Task.CompletedTask; },
      schemaReadyGate: new FakeGate(ready: false),
      exemptPaths: ["/probe"]);

    await middleware.InvokeAsync(context);

    await Assert.That(nextCalled).IsTrue();
  }

  [Test]
  public async Task NotReady_NonExemptPath_Returns503Async() {
    var nextCalled = false;
    var context = new DefaultHttpContext();
    context.Request.Path = "/api/jobs";
    context.Response.Body = new MemoryStream();

    var middleware = new DatabaseAvailabilityMiddleware(
      next: _ => { nextCalled = true; return Task.CompletedTask; },
      schemaReadyGate: new FakeGate(ready: false));

    await middleware.InvokeAsync(context);

    // A real API path is still gated with 503 while the schema is not ready.
    await Assert.That(nextCalled).IsFalse();
    await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status503ServiceUnavailable);
  }
}
