using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Verifies the correlation middleware captures a Guid-parseable inbound header into
/// <see cref="InboundCorrelationAccessor"/> before the rest of the pipeline runs, and leaves it null otherwise.
/// </summary>
public class WhizbangCorrelationMiddlewareTests {

  // Builds the middleware with the given header names; pass none to leave the options empty (exercising the
  // fallback to the default X-Correlation-ID).
  private static WhizbangCorrelationMiddleware _create(RequestDelegate next, params string[] headers) {
    var options = new WhizbangCorrelationOptions();
    options.HeaderNames.Clear();
    foreach (var header in headers) {
      options.HeaderNames.Add(header);
    }
    return new WhizbangCorrelationMiddleware(next, Options.Create(options));
  }

  [Test]
  public async Task Invoke_WithUuidV7CorrelationHeader_SeedsInboundAccessorBeforeNextAsync() {
    InboundCorrelationAccessor.Current = null;
    var expected = CorrelationId.New(); // UUIDv7
    CorrelationId? capturedDuringPipeline = null;

    var middleware = _create(
      _ => { capturedDuringPipeline = InboundCorrelationAccessor.Current; return Task.CompletedTask; },
      "X-Correlation-ID");

    var context = new DefaultHttpContext();
    context.Request.Headers["X-Correlation-ID"] = expected.Value.ToString();

    try {
      await middleware.InvokeAsync(context);

      await Assert.That(capturedDuringPipeline).IsEqualTo(expected)
        .Because("The header correlation id must be seeded before the rest of the pipeline runs.");
    } finally {
      InboundCorrelationAccessor.Current = null;
    }
  }

  [Test]
  public async Task Invoke_WithUuidV4Header_AdoptsItAsExternalTokenAsync() {
    // The browser's crypto.randomUUID() is UUIDv4; a W3C trace-id is 128 bits — both fit a Guid and are
    // adopted verbatim as external correlation tokens.
    InboundCorrelationAccessor.Current = null;
    var v4 = Guid.NewGuid(); // UUIDv4
    CorrelationId? captured = null;
    var middleware = _create(
      _ => { captured = InboundCorrelationAccessor.Current; return Task.CompletedTask; },
      "X-Correlation-ID");
    var context = new DefaultHttpContext();
    context.Request.Headers["X-Correlation-ID"] = v4.ToString();

    try {
      await middleware.InvokeAsync(context);

      await Assert.That(captured).IsEqualTo(CorrelationId.FromExternal(v4))
        .Because("A client-supplied v4 correlation token must be adopted, not dropped.");
    } finally {
      InboundCorrelationAccessor.Current = null;
    }
  }

  [Test]
  public async Task Invoke_WithNoHeader_LeavesInboundAccessorNullAsync() {
    InboundCorrelationAccessor.Current = null;
    var middleware = _create(_ => Task.CompletedTask, "X-Correlation-ID");

    await middleware.InvokeAsync(new DefaultHttpContext());

    await Assert.That(InboundCorrelationAccessor.Current).IsNull();
  }

  [Test]
  public async Task Invoke_WithNonGuidHeader_LeavesInboundAccessorNullAsync() {
    InboundCorrelationAccessor.Current = null;
    var middleware = _create(_ => Task.CompletedTask, "X-Correlation-ID");
    var context = new DefaultHttpContext();
    context.Request.Headers["X-Correlation-ID"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    await middleware.InvokeAsync(context);

    await Assert.That(InboundCorrelationAccessor.Current).IsNull()
      .Because("A non-Guid token (W3C traceparent format) is ignored.");
  }

  [Test]
  public async Task Invoke_EmptyHeaderNames_FallsBackToDefaultAsync() {
    InboundCorrelationAccessor.Current = null;
    var expected = CorrelationId.New(); // UUIDv7
    CorrelationId? captured = null;
    var middleware = _create(
      _ => { captured = InboundCorrelationAccessor.Current; return Task.CompletedTask; });

    var context = new DefaultHttpContext();
    context.Request.Headers["X-Correlation-ID"] = expected.Value.ToString();

    try {
      await middleware.InvokeAsync(context);

      await Assert.That(captured).IsEqualTo(expected);
    } finally {
      InboundCorrelationAccessor.Current = null;
    }
  }
}
