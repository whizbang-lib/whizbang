namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Options for <see cref="WhizbangSecurityHeadersMiddleware"/> — the hardened response headers to emit and
/// the HTTP methods to accept. Every header value is overridable; set a value to <c>null</c> to suppress
/// that header entirely. Defaults follow current OWASP secure-headers guidance. Configure via the
/// <c>UseWhizbangSecurityHeaders(options =&gt; ...)</c> overload.
/// </summary>
/// <docs>fundamentals/security/http-security-headers</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangSecurityHeadersMiddlewareTests.cs</tests>
public sealed class WhizbangSecurityHeadersOptions {
  /// <summary>
  /// Value for <c>Strict-Transport-Security</c>. Only emitted when the request is HTTPS or arrived through
  /// a TLS-terminating proxy (<c>X-Forwarded-Proto: https</c>). <c>null</c> suppresses the header.
  /// </summary>
  public string? StrictTransportSecurity { get; set; } = "max-age=31536000; includeSubDomains; preload";

  /// <summary>Value for <c>X-Content-Type-Options</c>. <c>null</c> suppresses the header.</summary>
  public string? XContentTypeOptions { get; set; } = "nosniff";

  /// <summary>Value for <c>X-Frame-Options</c>. <c>null</c> suppresses the header.</summary>
  public string? XFrameOptions { get; set; } = "DENY";

  /// <summary>
  /// Value for <c>Content-Security-Policy</c>. The default only forbids framing (<c>frame-ancestors</c>);
  /// services that serve HTML should replace it with a full policy. <c>null</c> suppresses the header.
  /// </summary>
  public string? ContentSecurityPolicy { get; set; } = "frame-ancestors 'none'";

  /// <summary>Value for <c>Referrer-Policy</c>. <c>null</c> suppresses the header.</summary>
  public string? ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

  /// <summary>Value for <c>Permissions-Policy</c>. <c>null</c> suppresses the header.</summary>
  public string? PermissionsPolicy { get; set; } = "camera=(), microphone=(), geolocation=()";

  /// <summary>
  /// HTTP methods the service accepts; any other method is rejected with <c>405 Method Not Allowed</c>
  /// before reaching routing. Comparison is case-insensitive.
  /// </summary>
  public IList<string> AllowedMethods { get; } = new List<string> { "GET", "HEAD", "POST", "OPTIONS" };
}
