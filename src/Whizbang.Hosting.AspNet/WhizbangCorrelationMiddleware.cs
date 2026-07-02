using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// ASP.NET Core middleware that captures an inbound correlation id from a configured request header (default
/// <c>X-Correlation-ID</c>) into <see cref="InboundCorrelationAccessor"/>, so the first message dispatched
/// during the request adopts the client-supplied correlation id instead of minting a fresh one. Runs for the
/// whole HTTP pipeline, so it covers both REST (FastEndpoints) and GraphQL (HotChocolate) entry points.
/// </summary>
/// <remarks>
/// Auto-wired by <see cref="ServiceCollectionExtensions.AddWhizbangAspNet"/> via an <c>IStartupFilter</c> — no
/// manual pipeline registration is needed. A W3C trace-id is 128 bits, so it (like any UUID, including the
/// browser's <c>crypto.randomUUID</c> v4) fits a <see cref="System.Guid"/> and is adopted verbatim.
/// </remarks>
/// <docs>fundamentals/messages/message-context</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangCorrelationMiddlewareTests.cs</tests>
public class WhizbangCorrelationMiddleware {
  private readonly RequestDelegate _next;
  private readonly string[] _headerNames;

  /// <summary>
  /// Creates the middleware, reading the header names to inspect from <paramref name="options"/>
  /// (falling back to <c>X-Correlation-ID</c> when none are configured).
  /// </summary>
  public WhizbangCorrelationMiddleware(RequestDelegate next, IOptions<WhizbangCorrelationOptions> options) {
    _next = next;
    var names = options.Value.HeaderNames;
    _headerNames = names is { Count: > 0 } ? [.. names] : ["X-Correlation-ID"];
  }

  /// <summary>
  /// Reads the correlation header and seeds <see cref="InboundCorrelationAccessor"/> before invoking the rest
  /// of the pipeline.
  /// </summary>
  public async Task InvokeAsync(HttpContext context) {
    foreach (var headerName in _headerNames) {
      // A W3C trace-id is 128 bits, so it (like any UUID, including the browser's crypto.randomUUID v4) fits
      // a Guid; adopt it verbatim as an external correlation token. Non-Guid tokens are ignored.
      if (context.Request.Headers.TryGetValue(headerName, out var value)
          && Guid.TryParse(value.ToString(), out var guid)) {
        InboundCorrelationAccessor.Current = CorrelationId.FromExternal(guid);
        break;
      }
    }

    await _next(context);
  }
}
