using System.Text;
using Microsoft.AspNetCore.Http;
using Whizbang.Core.Messaging;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// ASP.NET Core middleware that gates requests on database availability.
/// Returns 503 Service Unavailable with Retry-After header when the database
/// is unreachable, preventing request storms from reaching the DB layer.
/// </summary>
/// <remarks>
/// Works best with a circuit-breaker-wrapped <see cref="IDatabaseReadinessCheck"/>
/// so the check returns instantly when the circuit is open (no network I/O per request).
/// </remarks>
/// <docs>resilience/database-availability-middleware</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/DatabaseAvailabilityMiddlewareTests.cs</tests>
public class DatabaseAvailabilityMiddleware(RequestDelegate next, IDatabaseReadinessCheck readinessCheck) {
  private static readonly byte[] _responseBody = Encoding.UTF8.GetBytes(
    """{"error":"Service temporarily unavailable","reason":"database_unreachable"}""");

  /// <summary>
  /// Checks database readiness before passing the request to the next middleware.
  /// Returns 503 with a JSON error body and Retry-After header if the database is unavailable.
  /// </summary>
  public async Task InvokeAsync(HttpContext context) {
    if (!await readinessCheck.IsReadyAsync(context.RequestAborted)) {
      context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
      context.Response.Headers.RetryAfter = "30";
      context.Response.ContentType = "application/json";
      await context.Response.Body.WriteAsync(_responseBody, context.RequestAborted);
      return;
    }

    await next(context);
  }
}
