using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Registration helpers for <see cref="WhizbangSecurityHeadersMiddleware"/>.
/// </summary>
/// <docs>fundamentals/security/http-security-headers</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangSecurityHeadersMiddlewareTests.cs</tests>
public static class WhizbangSecurityHeadersMiddlewareExtensions {
  /// <summary>
  /// Adds the security-headers + method-filter middleware to the pipeline. Place it <b>before</b>
  /// <c>UseRouting()</c> so error responses (401/404/405) are hardened too. Opt-in by design — it is not
  /// registered by <see cref="ServiceCollectionExtensions.AddWhizbangAspNet"/>.
  /// </summary>
  /// <param name="app">The application pipeline builder.</param>
  /// <param name="configure">Optional per-service overrides of <see cref="WhizbangSecurityHeadersOptions"/>.</param>
  /// <returns>The builder for chaining.</returns>
  public static IApplicationBuilder UseWhizbangSecurityHeaders(
      this IApplicationBuilder app,
      Action<WhizbangSecurityHeadersOptions>? configure = null) {
    var options = new WhizbangSecurityHeadersOptions();
    configure?.Invoke(options);
    return app.UseMiddleware<WhizbangSecurityHeadersMiddleware>(Options.Create(options));
  }
}
