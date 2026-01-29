namespace Whizbang.Core.Security;

/// <summary>
/// Type-safe identifier for security principals (users, groups, services, applications).
/// Enables group-based access control with nested group support.
/// </summary>
/// <docs>core-concepts/security#security-principals</docs>
/// <tests>Whizbang.Core.Tests/Security/SecurityPrincipalIdTests.cs</tests>
/// <example>
/// // Create principals with type prefixes
/// var user = SecurityPrincipalId.User("alice");           // "user:alice"
/// var group = SecurityPrincipalId.Group("sales-team");    // "group:sales-team"
/// var service = SecurityPrincipalId.Service("api");       // "svc:api"
/// var app = SecurityPrincipalId.Application("mobile");    // "app:mobile"
///
/// // Check principal type
/// user.IsUser;    // true
/// group.IsGroup;  // true
///
/// // Use in access control
/// var allowedPrincipals = new HashSet&lt;SecurityPrincipalId&gt; {
///   SecurityPrincipalId.Group("sales-team"),
///   SecurityPrincipalId.User("manager-456")
/// };
/// </example>
public readonly record struct SecurityPrincipalId(string Value) : IEquatable<SecurityPrincipalId> {
  /// <summary>
  /// Implicitly converts a SecurityPrincipalId to its string value.
  /// </summary>
  public static implicit operator string(SecurityPrincipalId id) => id.Value;

  /// <summary>
  /// Implicitly converts a string to a SecurityPrincipalId.
  /// </summary>
  public static implicit operator SecurityPrincipalId(string s) => new(s);

  /// <summary>
  /// Creates a user principal identifier.
  /// </summary>
  /// <param name="userId">The user identifier.</param>
  /// <returns>A principal like "user:alice".</returns>
  public static SecurityPrincipalId User(string userId) => new($"user:{userId}");

  /// <summary>
  /// Creates a group principal identifier.
  /// </summary>
  /// <param name="groupId">The group identifier.</param>
  /// <returns>A principal like "group:sales-team".</returns>
  public static SecurityPrincipalId Group(string groupId) => new($"group:{groupId}");

  /// <summary>
  /// Creates a service principal identifier.
  /// </summary>
  /// <param name="serviceId">The service identifier.</param>
  /// <returns>A principal like "svc:payment-processor".</returns>
  public static SecurityPrincipalId Service(string serviceId) => new($"svc:{serviceId}");

  /// <summary>
  /// Creates an application principal identifier.
  /// </summary>
  /// <param name="appId">The application identifier.</param>
  /// <returns>A principal like "app:mobile-app".</returns>
  public static SecurityPrincipalId Application(string appId) => new($"app:{appId}");

  /// <summary>
  /// Returns true if this is a user principal.
  /// </summary>
  public bool IsUser => Value.StartsWith("user:", StringComparison.Ordinal);

  /// <summary>
  /// Returns true if this is a group principal.
  /// </summary>
  public bool IsGroup => Value.StartsWith("group:", StringComparison.Ordinal);

  /// <summary>
  /// Returns true if this is a service principal.
  /// </summary>
  public bool IsService => Value.StartsWith("svc:", StringComparison.Ordinal);

  /// <summary>
  /// Returns true if this is an application principal.
  /// </summary>
  public bool IsApplication => Value.StartsWith("app:", StringComparison.Ordinal);

  /// <summary>
  /// Returns the principal value as a string.
  /// </summary>
  public override string ToString() => Value;
}
