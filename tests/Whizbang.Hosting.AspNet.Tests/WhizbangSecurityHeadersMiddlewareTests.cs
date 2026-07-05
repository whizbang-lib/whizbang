using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Verifies the security-headers middleware applies hardened response headers (HSTS only over TLS or
/// forwarded-TLS), never overwrites headers already present (edge wins), and short-circuits disallowed
/// HTTP methods with 405 before the rest of the pipeline runs.
/// </summary>
public class WhizbangSecurityHeadersMiddlewareTests {

  /// <summary>
  /// <see cref="DefaultHttpContext"/> never invokes <c>Response.OnStarting</c> callbacks itself, so this
  /// feature records them and lets tests fire them the way a real server would just before the response starts.
  /// </summary>
  private sealed class StartableResponseFeature : HttpResponseFeature {
    private readonly List<(Func<object, Task> Callback, object State)> _callbacks = [];

    public override void OnStarting(Func<object, Task> callback, object state) {
      _callbacks.Add((callback, state));
    }

    public async Task FireOnStartingAsync() {
      // Real servers run these LIFO, but header application is order-independent here.
      foreach (var (callback, state) in _callbacks) {
        await callback(state);
      }
    }
  }

  private static (WhizbangSecurityHeadersMiddleware Middleware, DefaultHttpContext Context, StartableResponseFeature Response, List<bool> NextCalls) _create(
      Action<WhizbangSecurityHeadersOptions>? configure = null,
      string method = "GET") {
    var options = new WhizbangSecurityHeadersOptions();
    configure?.Invoke(options);

    var nextCalls = new List<bool>();
    var middleware = new WhizbangSecurityHeadersMiddleware(
      _ => { nextCalls.Add(true); return Task.CompletedTask; },
      Options.Create(options));

    var context = new DefaultHttpContext();
    var responseFeature = new StartableResponseFeature();
    context.Features.Set<IHttpResponseFeature>(responseFeature);
    context.Request.Method = method;

    return (middleware, context, responseFeature, nextCalls);
  }

  [Test]
  public async Task Invoke_PlainHttpGet_SetsAllDefaultHeadersExceptHstsAsync() {
    var (middleware, context, response, _) = _create();

    await middleware.InvokeAsync(context);
    await response.FireOnStartingAsync();

    await Assert.That(context.Response.Headers["X-Content-Type-Options"].ToString()).IsEqualTo("nosniff");
    await Assert.That(context.Response.Headers["X-Frame-Options"].ToString()).IsEqualTo("DENY");
    await Assert.That(context.Response.Headers["Content-Security-Policy"].ToString())
      .IsEqualTo("frame-ancestors 'none'");
    await Assert.That(context.Response.Headers["Referrer-Policy"].ToString())
      .IsEqualTo("strict-origin-when-cross-origin");
    await Assert.That(context.Response.Headers["Permissions-Policy"].ToString())
      .IsEqualTo("camera=(), microphone=(), geolocation=()");
    await Assert.That(context.Response.Headers.ContainsKey("Strict-Transport-Security")).IsFalse()
      .Because("HSTS on a plain-HTTP response is meaningless and can mask a misconfigured edge.");
  }

  [Test]
  public async Task Invoke_HttpsRequest_EmitsHstsAsync() {
    var (middleware, context, response, _) = _create();
    context.Request.IsHttps = true;

    await middleware.InvokeAsync(context);
    await response.FireOnStartingAsync();

    await Assert.That(context.Response.Headers["Strict-Transport-Security"].ToString())
      .IsEqualTo("max-age=31536000; includeSubDomains; preload");
  }

  [Test]
  public async Task Invoke_ForwardedHttpsRequest_EmitsHstsAsync() {
    // Pods listen on plain HTTP behind a TLS-terminating edge; the edge forwards the original scheme.
    var (middleware, context, response, _) = _create();
    context.Request.Headers["X-Forwarded-Proto"] = "https";

    await middleware.InvokeAsync(context);
    await response.FireOnStartingAsync();

    await Assert.That(context.Response.Headers["Strict-Transport-Security"].ToString())
      .IsEqualTo("max-age=31536000; includeSubDomains; preload");
  }

  [Test]
  public async Task Invoke_ForwardedProtoChain_UsesFirstHopSchemeAsync() {
    // Multiple proxies append: "https, http". The first (client-facing) hop decides.
    var (middleware, context, response, _) = _create();
    context.Request.Headers["X-Forwarded-Proto"] = "https, http";

    await middleware.InvokeAsync(context);
    await response.FireOnStartingAsync();

    await Assert.That(context.Response.Headers.ContainsKey("Strict-Transport-Security")).IsTrue()
      .Because("The client-facing hop was TLS, so HSTS applies.");
  }

  [Test]
  public async Task Invoke_HeaderAlreadyPresent_IsNotOverwrittenAsync() {
    var (middleware, context, response, _) = _create();
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";

    await middleware.InvokeAsync(context);
    await response.FireOnStartingAsync();

    await Assert.That(context.Response.Headers["X-Frame-Options"].ToString()).IsEqualTo("SAMEORIGIN")
      .Because("A header set upstream (e.g. by the edge or another middleware) must win; the middleware only fills gaps.");
  }

  [Test]
  public async Task Invoke_OptionSetToNull_SuppressesThatHeaderAsync() {
    var (middleware, context, response, _) = _create(o => o.PermissionsPolicy = null);

    await middleware.InvokeAsync(context);
    await response.FireOnStartingAsync();

    await Assert.That(context.Response.Headers.ContainsKey("Permissions-Policy")).IsFalse();
    await Assert.That(context.Response.Headers["X-Content-Type-Options"].ToString()).IsEqualTo("nosniff")
      .Because("Suppressing one header must not affect the others.");
  }

  [Test]
  public async Task Invoke_CustomHeaderValue_IsEmittedAsync() {
    var (middleware, context, response, _) = _create(o => o.ContentSecurityPolicy = "frame-ancestors 'self'");

    await middleware.InvokeAsync(context);
    await response.FireOnStartingAsync();

    await Assert.That(context.Response.Headers["Content-Security-Policy"].ToString())
      .IsEqualTo("frame-ancestors 'self'");
  }

  [Test]
  public async Task Invoke_DisallowedMethod_Returns405WithoutInvokingNextAsync() {
    var (middleware, context, response, nextCalls) = _create(method: "PUT");

    await middleware.InvokeAsync(context);
    await response.FireOnStartingAsync();

    await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status405MethodNotAllowed);
    await Assert.That(nextCalls).IsEmpty()
      .Because("A disallowed method must be short-circuited before reaching routing.");
    await Assert.That(context.Response.Headers["Allow"].ToString()).IsEqualTo("GET, HEAD, POST, OPTIONS");
  }

  [Test]
  public async Task Invoke_DisallowedMethod_StillGetsSecurityHeadersAsync() {
    var (middleware, context, response, _) = _create(method: "TRACE");

    await middleware.InvokeAsync(context);
    await response.FireOnStartingAsync();

    await Assert.That(context.Response.Headers["X-Content-Type-Options"].ToString()).IsEqualTo("nosniff")
      .Because("Error responses need hardening too — the pentest flagged 404/405 responses specifically.");
  }

  [Test]
  public async Task Invoke_AllowedMethod_InvokesNextAsync() {
    var (middleware, context, _, nextCalls) = _create(method: "POST");

    await middleware.InvokeAsync(context);

    await Assert.That(nextCalls.Count).IsEqualTo(1);
    await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
  }

  [Test]
  public async Task Invoke_MethodComparison_IsCaseInsensitiveAsync() {
    var (middleware, context, _, nextCalls) = _create(method: "get");

    await middleware.InvokeAsync(context);

    await Assert.That(nextCalls.Count).IsEqualTo(1)
      .Because("HTTP method matching must not depend on client casing.");
  }

  [Test]
  public async Task Invoke_ExtendedAllowedMethods_PassesThroughAsync() {
    var (middleware, context, _, nextCalls) = _create(o => o.AllowedMethods.Add("DELETE"), method: "DELETE");

    await middleware.InvokeAsync(context);

    await Assert.That(nextCalls.Count).IsEqualTo(1);
    await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
  }

  [Test]
  public async Task Options_Defaults_MatchHardenedBaselineAsync() {
    var options = new WhizbangSecurityHeadersOptions();

    await Assert.That(options.StrictTransportSecurity).IsEqualTo("max-age=31536000; includeSubDomains; preload");
    await Assert.That(options.XContentTypeOptions).IsEqualTo("nosniff");
    await Assert.That(options.XFrameOptions).IsEqualTo("DENY");
    await Assert.That(options.ContentSecurityPolicy).IsEqualTo("frame-ancestors 'none'");
    await Assert.That(options.ReferrerPolicy).IsEqualTo("strict-origin-when-cross-origin");
    await Assert.That(options.PermissionsPolicy).IsEqualTo("camera=(), microphone=(), geolocation=()");
    await Assert.That(options.AllowedMethods).IsEquivalentTo(["GET", "HEAD", "POST", "OPTIONS"]);
  }
}
