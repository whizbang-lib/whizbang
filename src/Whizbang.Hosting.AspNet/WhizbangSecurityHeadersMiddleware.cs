using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// ASP.NET Core middleware that applies hardened HTTP response headers and rejects unexpected HTTP methods
/// with <c>405</c>. Headers are applied via <c>Response.OnStarting</c>, so they cover every response the
/// pipeline produces — REST, GraphQL, health probes, and framework-generated errors (401/404/405) alike.
/// A header already present on the response is never overwritten (the edge wins), and
/// <c>Strict-Transport-Security</c> is only emitted on TLS or forwarded-TLS requests, because pods
/// typically listen on plain HTTP behind a TLS-terminating edge. Reflection-free and AOT-safe.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> auto-wired by <see cref="ServiceCollectionExtensions.AddWhizbangAspNet"/> —
/// silently adding <c>X-Frame-Options: DENY</c> to every consumer would be a breaking change. Enable it
/// per service with <see cref="WhizbangSecurityHeadersMiddlewareExtensions.UseWhizbangSecurityHeaders"/>.
/// </remarks>
/// <docs>fundamentals/security/http-security-headers</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangSecurityHeadersMiddlewareTests.cs</tests>
public class WhizbangSecurityHeadersMiddleware {
  private readonly RequestDelegate _next;
  private readonly WhizbangSecurityHeadersOptions _options;
  private readonly HashSet<string> _allowedMethods;
  private readonly string _allowHeaderValue;

  /// <summary>
  /// Creates the middleware, snapshotting the configured headers and allowed methods.
  /// </summary>
  public WhizbangSecurityHeadersMiddleware(RequestDelegate next, IOptions<WhizbangSecurityHeadersOptions> options) {
    _next = next;
    _options = options.Value;
    _allowedMethods = new HashSet<string>(_options.AllowedMethods, StringComparer.OrdinalIgnoreCase);
    _allowHeaderValue = string.Join(", ", _options.AllowedMethods);
  }

  /// <summary>
  /// Registers the header callback for this response, then either short-circuits a disallowed method with
  /// <c>405</c> (plus an <c>Allow</c> header) or invokes the rest of the pipeline.
  /// </summary>
  public Task InvokeAsync(HttpContext context) {
    // Opt-out: turnkey registration means every consumer gets this middleware, so a disabled instance
    // must be a transparent pass-through.
    if (!_options.Enabled) {
      return _next(context);
    }

    // Static callback + explicit state keeps this allocation-light and reflection-free.
    context.Response.OnStarting(static state => {
      var (middleware, httpContext) = ((WhizbangSecurityHeadersMiddleware, HttpContext))state;
      middleware._applyHeaders(httpContext);
      return Task.CompletedTask;
    }, (this, context));

    // Method filtering is opt-in: only enforced when an allowlist is configured. Empty = allow all verbs,
    // so turnkey wiring never 405s a service that legitimately serves PUT/PATCH/DELETE.
    if (_allowedMethods.Count > 0 && !_allowedMethods.Contains(context.Request.Method)) {
      context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
      context.Response.Headers.Allow = _allowHeaderValue;
      return Task.CompletedTask;
    }

    return _next(context);
  }

  private void _applyHeaders(HttpContext context) {
    var headers = context.Response.Headers;

    if (_options.StrictTransportSecurity is not null && _isTlsRequest(context.Request)) {
      _setIfAbsent(headers, "Strict-Transport-Security", _options.StrictTransportSecurity);
    }
    _setIfAbsent(headers, "X-Content-Type-Options", _options.XContentTypeOptions);
    _setIfAbsent(headers, "X-Frame-Options", _options.XFrameOptions);
    _setIfAbsent(headers, "Content-Security-Policy", _options.ContentSecurityPolicy);
    _setIfAbsent(headers, "Referrer-Policy", _options.ReferrerPolicy);
    _setIfAbsent(headers, "Permissions-Policy", _options.PermissionsPolicy);
  }

  private static void _setIfAbsent(IHeaderDictionary headers, string name, string? value) {
    if (value is not null && !headers.ContainsKey(name)) {
      headers[name] = value;
    }
  }

  // HSTS is only meaningful over TLS. Behind a TLS-terminating edge the pod sees plain HTTP, so the
  // client-facing (first) X-Forwarded-Proto hop decides.
  private static bool _isTlsRequest(HttpRequest request) {
    if (request.IsHttps) {
      return true;
    }
    if (!request.Headers.TryGetValue("X-Forwarded-Proto", out StringValues forwardedProto)) {
      return false;
    }
    var firstHop = forwardedProto.ToString().Split(',')[0].Trim();
    return string.Equals(firstHop, "https", StringComparison.OrdinalIgnoreCase);
  }
}
