using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Startup filter that automatically injects <see cref="WhizbangSecurityHeadersMiddleware"/> at the very
/// start of the request pipeline, so hardened response headers cover every response — including
/// framework-generated errors (401/404/405) produced before routing. Registered by
/// <see cref="ServiceCollectionExtensions.AddWhizbangAspNet"/> — security headers are turnkey.
/// <para>
/// Safe defaults: headers are idempotent (an edge-set header is never overwritten), HSTS is emitted only
/// over TLS/forwarded-TLS, and method filtering is OFF unless a consumer populates
/// <see cref="WhizbangSecurityHeadersOptions.AllowedMethods"/>. Opt out entirely via
/// <see cref="WhizbangSecurityHeadersOptions.Enabled"/> = <c>false</c>.
/// </para>
/// </summary>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangSecurityHeadersStartupFilterTests.cs</tests>
internal sealed class WhizbangSecurityHeadersStartupFilter : IStartupFilter {
  public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) {
    return app => {
      app.UseMiddleware<WhizbangSecurityHeadersMiddleware>();
      next(app);
    };
  }
}
