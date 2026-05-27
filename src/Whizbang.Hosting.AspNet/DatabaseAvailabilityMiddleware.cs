using System.Text;
using Microsoft.AspNetCore.Http;
using Whizbang.Core.Workers;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// ASP.NET Core middleware that returns 503 Service Unavailable until the
/// <see cref="ISchemaReadyGate"/> signals that database migrations have completed.
/// Once ready, the middleware is a pass-through.
/// </summary>
/// <docs>resilience/database-availability-middleware</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/DatabaseAvailabilityMiddlewareTests.cs</tests>
public class DatabaseAvailabilityMiddleware(RequestDelegate next, ISchemaReadyGate schemaReadyGate) {
  private static readonly byte[] _responseBody = Encoding.UTF8.GetBytes(
    """{"error":"Service temporarily unavailable","reason":"schema_initializing"}""");

  /// <summary>
  /// Checks schema readiness before passing the request to the next middleware.
  /// Returns 503 with a JSON error body and Retry-After header if the schema gate has not yet signaled ready.
  /// </summary>
  public async Task InvokeAsync(HttpContext context) {
    if (!schemaReadyGate.IsReady) {
      context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
      context.Response.Headers.RetryAfter = "30";
      context.Response.ContentType = "application/json";
      await context.Response.Body.WriteAsync(_responseBody, context.RequestAborted);
      return;
    }

    await next(context);
  }
}
