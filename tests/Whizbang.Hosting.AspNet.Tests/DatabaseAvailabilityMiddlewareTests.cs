using Microsoft.AspNetCore.Http;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Hosting.AspNet;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Tests for DatabaseAvailabilityMiddleware.
/// Validates 503 response when database is unavailable and passthrough when ready.
/// </summary>
public class DatabaseAvailabilityMiddlewareTests {

  [Test]
  public async Task InvokeAsync_DatabaseReady_PassesThroughAsync() {
    var readinessCheck = new TestReadinessCheck(isReady: true);
    var nextCalled = false;
    var middleware = new DatabaseAvailabilityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, readinessCheck);

    var context = new DefaultHttpContext();
    await middleware.InvokeAsync(context);

    await Assert.That(nextCalled).IsTrue()
      .Because("Request should pass through when database is ready");
    await Assert.That(context.Response.StatusCode).IsEqualTo(200);
  }

  [Test]
  public async Task InvokeAsync_DatabaseNotReady_Returns503Async() {
    var readinessCheck = new TestReadinessCheck(isReady: false);
    var nextCalled = false;
    var middleware = new DatabaseAvailabilityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, readinessCheck);

    var context = new DefaultHttpContext();
    context.Response.Body = new MemoryStream();
    await middleware.InvokeAsync(context);

    await Assert.That(nextCalled).IsFalse()
      .Because("Request should NOT reach next middleware when database is unavailable");
    await Assert.That(context.Response.StatusCode).IsEqualTo(503);
  }

  [Test]
  public async Task InvokeAsync_DatabaseNotReady_IncludesRetryAfterHeaderAsync() {
    var readinessCheck = new TestReadinessCheck(isReady: false);
    var middleware = new DatabaseAvailabilityMiddleware(_ => Task.CompletedTask, readinessCheck);

    var context = new DefaultHttpContext();
    context.Response.Body = new MemoryStream();
    await middleware.InvokeAsync(context);

    await Assert.That(context.Response.Headers.RetryAfter.ToString()).IsNotEmpty()
      .Because("503 should include Retry-After header");
  }

  [Test]
  public async Task InvokeAsync_DatabaseNotReady_SetsJsonContentTypeAsync() {
    var readinessCheck = new TestReadinessCheck(isReady: false);
    var middleware = new DatabaseAvailabilityMiddleware(_ => Task.CompletedTask, readinessCheck);

    var context = new DefaultHttpContext();
    context.Response.Body = new MemoryStream();
    await middleware.InvokeAsync(context);

    await Assert.That(context.Response.ContentType).IsNotNull().And.Contains("application/json");
  }

  [Test]
  public async Task InvokeAsync_DatabaseNotReady_ResponseBodyContainsErrorAsync() {
    var readinessCheck = new TestReadinessCheck(isReady: false);
    var middleware = new DatabaseAvailabilityMiddleware(_ => Task.CompletedTask, readinessCheck);

    var context = new DefaultHttpContext();
    context.Response.Body = new MemoryStream();
    await middleware.InvokeAsync(context);

    context.Response.Body.Seek(0, SeekOrigin.Begin);
    var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
    await Assert.That(body).Contains("unavailable");
  }

  private sealed class TestReadinessCheck(bool isReady) : IDatabaseReadinessCheck {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(isReady);
  }
}
