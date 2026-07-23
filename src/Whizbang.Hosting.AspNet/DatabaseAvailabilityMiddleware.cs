using System.Text;
using Microsoft.AspNetCore.Http;
using Whizbang.Core.Workers;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// ASP.NET Core middleware that returns 503 Service Unavailable until <see cref="ISchemaReadyGate"/>
/// signals that database migrations have completed — <b>except</b> for a configurable set of exempt
/// paths (the health/probe endpoints), which always pass through. This lets a host that binds its
/// port before migrations finish (non-blocking schema init) keep answering its liveness/readiness
/// probes while still rejecting real API traffic until the schema is ready. Once ready, the middleware
/// is a pass-through. Place it early in the pipeline (before routing) to reject requests fast.
/// </summary>
/// <docs>resilience/database-availability-middleware</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/DatabaseAvailabilityMiddlewareTests.cs</tests>
public class DatabaseAvailabilityMiddleware {
  /// <summary>
  /// Default exempt path prefixes: the standard liveness (<c>/alive</c>), readiness (<c>/health</c>)
  /// and version (<c>/version</c>) endpoints. A request whose path starts with one of these is never
  /// gated, so probes keep working while the schema initializes.
  /// </summary>
  public static readonly IReadOnlyList<string> DefaultExemptPaths = ["/alive", "/health", "/version"];

  private static readonly byte[] _responseBody = Encoding.UTF8.GetBytes(
    """{"error":"Service temporarily unavailable","reason":"schema_initializing"}""");

  private readonly RequestDelegate _next;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly PathString[] _exemptPaths;
  private readonly AvailabilityGateMode _mode;

  /// <summary>
  /// Creates the middleware. <paramref name="exemptPaths"/> is the set of path prefixes that always
  /// pass through even before the schema is ready (<see langword="null"/> uses
  /// <see cref="DefaultExemptPaths"/>); <paramref name="mode"/> selects whether every non-exempt
  /// request is gated or only mutating ones (see <see cref="AvailabilityGateMode"/>).
  /// </summary>
  public DatabaseAvailabilityMiddleware(
      RequestDelegate next, ISchemaReadyGate schemaReadyGate, IReadOnlyList<string>? exemptPaths = null,
      AvailabilityGateMode mode = AvailabilityGateMode.AllNonExempt) {
    _next = next;
    _schemaReadyGate = schemaReadyGate;
    _exemptPaths = [.. (exemptPaths ?? DefaultExemptPaths).Select(static p => new PathString(p))];
    _mode = mode;
  }

  /// <summary>
  /// Returns 503 with a JSON error body and Retry-After header when the schema gate has not yet
  /// signaled ready and the request is gated (not exempt, and — in <see cref="AvailabilityGateMode.MutationsOnly"/>
  /// mode — a mutating request); otherwise delegates to the next middleware.
  /// </summary>
  public async Task InvokeAsync(HttpContext context) {
    if (!_schemaReadyGate.IsReady && !_isExempt(context.Request.Path) && _isGated(context.Request.Method)) {
      context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
      context.Response.Headers.RetryAfter = "30";
      context.Response.ContentType = "application/json";
      await context.Response.Body.WriteAsync(_responseBody, context.RequestAborted);
      return;
    }

    await _next(context);
  }

  private bool _isGated(string method) => _mode switch {
    AvailabilityGateMode.MutationsOnly =>
      !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)),
    _ => true
  };

  private bool _isExempt(PathString path) {
    foreach (var exempt in _exemptPaths) {
      if (path.StartsWithSegments(exempt, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
    }
    return false;
  }
}
