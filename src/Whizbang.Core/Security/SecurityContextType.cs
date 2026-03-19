namespace Whizbang.Core.Security;

/// <summary>
/// Indicates how security context was established for the current operation.
/// Used for audit trail and security policy enforcement.
/// </summary>
/// <docs>fundamentals/security/message-security#explicit-security-context-api</docs>
/// <tests>Whizbang.Core.Tests/Dispatch/DispatcherSecurityBuilderTests.cs</tests>
public enum SecurityContextType {
  /// <summary>
  /// User-initiated operation from HTTP request or message with user identity.
  /// This is the default context type for normal user operations.
  /// </summary>
  User = 0,

  /// <summary>
  /// System-initiated operation with no user involvement (timers, schedulers, background jobs).
  /// EffectivePrincipal is "SYSTEM", ActualPrincipal may be null (true system op) or
  /// the user who triggered it (admin clicking "Run as System").
  /// </summary>
  System = 1,

  /// <summary>
  /// User running as a different identity (impersonation with full audit trail).
  /// Both ActualPrincipal and EffectivePrincipal are captured for security auditing.
  /// Example: Support staff impersonating a customer to debug an issue.
  /// </summary>
  Impersonated = 2,

  /// <summary>
  /// Service-to-service call with service account identity.
  /// Used for inter-service communication with workload identity.
  /// </summary>
  ServiceAccount = 3
}
