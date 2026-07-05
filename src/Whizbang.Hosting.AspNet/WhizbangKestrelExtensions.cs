using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Kestrel hardening helpers. Currently suppresses the <c>Server: Kestrel</c> response banner, which
/// discloses the server implementation to anonymous callers for no operational benefit.
/// </summary>
/// <docs>fundamentals/security/http-security-headers</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangKestrelExtensionsTests.cs</tests>
public static class WhizbangKestrelExtensions {
  /// <summary>
  /// Applies Whizbang's Kestrel security defaults to the web host (currently
  /// <see cref="KestrelServerOptions.AddServerHeader"/> = <c>false</c>).
  /// </summary>
  /// <param name="builder">The web host builder.</param>
  /// <returns>The builder for chaining.</returns>
  public static IWebHostBuilder UseWhizbangKestrelSecurityDefaults(this IWebHostBuilder builder)
      => builder.ConfigureKestrel(ApplyWhizbangSecurityDefaults);

  /// <summary>
  /// Applies Whizbang's Kestrel security defaults to an options instance — usable directly from a
  /// <c>ConfigureKestrel</c> callback the host already owns.
  /// </summary>
  /// <param name="options">The Kestrel server options to harden.</param>
  public static void ApplyWhizbangSecurityDefaults(this KestrelServerOptions options) {
    options.AddServerHeader = false;
  }
}
