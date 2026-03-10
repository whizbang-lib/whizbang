namespace Whizbang.Core.Security;

/// <summary>
/// Configuration options for message security context establishment.
/// </summary>
/// <remarks>
/// Default configuration follows the principle of least privilege:
/// - AllowAnonymous is false by default (must explicitly opt-in)
/// - ValidateCredentials is true by default
/// - PropagateToOutgoingMessages is true by default
/// </remarks>
/// <docs>core-concepts/message-security#configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageSecurityOptionsTests.cs</tests>
/// <example>
/// services.AddWhizbangMessageSecurity(options => {
///   // Must explicitly opt-in to allow anonymous messages
///   options.AllowAnonymous = false; // This is the default
///
///   // Exempt specific message types (explicit registration, no reflection)
///   options.ExemptMessageTypes.Add(typeof(HealthCheckMessage));
///   options.ExemptMessageTypes.Add(typeof(SystemDiagnosticMessage));
///
///   // Adjust timeout for slow token validation
///   options.Timeout = TimeSpan.FromSeconds(10);
/// });
/// </example>
public sealed class MessageSecurityOptions {
  /// <summary>
  /// When true, allows messages without security context to be processed.
  /// DEFAULT: FALSE (least privilege - must explicitly enable).
  /// </summary>
  /// <remarks>
  /// Setting this to true allows messages through even when no extractor
  /// can establish a security context. The IScopeContextAccessor.Current
  /// will be null or contain an empty context.
  ///
  /// Consider using ExemptMessageTypes instead for specific message types
  /// that don't require security (e.g., health checks).
  /// </remarks>
  public bool AllowAnonymous { get; set; }

  /// <summary>
  /// When true, logs security context establishment for audit.
  /// DEFAULT: TRUE.
  /// </summary>
  public bool EnableAuditLogging { get; set; } = true;

  /// <summary>
  /// When true, extractors should validate tokens/credentials.
  /// DEFAULT: TRUE.
  /// </summary>
  /// <remarks>
  /// When true, extractors that handle tokens (JWT, etc.) should validate
  /// signatures, expiration, and other security properties.
  /// Set to false only in development/testing scenarios.
  /// </remarks>
  public bool ValidateCredentials { get; set; } = true;

  /// <summary>
  /// Maximum time to wait for security context establishment.
  /// DEFAULT: 5 seconds.
  /// </summary>
  /// <remarks>
  /// This timeout covers the entire extraction process, including all
  /// extractors that may be tried in priority order. Consider increasing
  /// this if your extractors perform external validation calls.
  /// </remarks>
  public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Message types exempt from security requirements.
  /// Must use explicit type registration (AOT compatible).
  /// </summary>
  /// <remarks>
  /// Exempt messages bypass the entire security context establishment process.
  /// The extractors are not called, and no context is established.
  /// Use this for infrastructure messages like health checks.
  /// </remarks>
  /// <example>
  /// options.ExemptMessageTypes.Add(typeof(HealthCheckMessage));
  /// </example>
  public HashSet<Type> ExemptMessageTypes { get; } = [];

  /// <summary>
  /// When true, propagates security context to cascaded/outgoing messages.
  /// DEFAULT: TRUE.
  /// </summary>
  /// <remarks>
  /// When enabled, the established security context is automatically
  /// included in MessageHop.Scope (via ScopeDelta) for outgoing messages,
  /// allowing downstream services to inherit the caller's identity.
  /// </remarks>
  public bool PropagateToOutgoingMessages { get; set; } = true;
}
