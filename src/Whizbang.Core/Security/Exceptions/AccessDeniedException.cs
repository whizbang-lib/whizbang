using Whizbang.Core.SystemEvents.Security;

namespace Whizbang.Core.Security.Exceptions;

/// <summary>
/// Thrown when access is denied due to insufficient permissions.
/// </summary>
/// <docs>core-concepts/security#exceptions</docs>
/// <tests>Whizbang.Core.Tests/Security/AccessDeniedExceptionTests.cs</tests>
public sealed class AccessDeniedException : Exception {
  /// <summary>
  /// The permission that was required for access.
  /// </summary>
  public Permission RequiredPermission { get; }

  /// <summary>
  /// Type of resource that access was denied to.
  /// </summary>
  public string ResourceType { get; }

  /// <summary>
  /// Optional identifier of the specific resource.
  /// </summary>
  public string? ResourceId { get; }

  /// <summary>
  /// Reason for the access denial.
  /// </summary>
  public AccessDenialReason Reason { get; }

  /// <summary>
  /// Creates a new AccessDeniedException with default message.
  /// </summary>
  public AccessDeniedException()
    : base("Access denied") {
    RequiredPermission = default;
    ResourceType = string.Empty;
    Reason = AccessDenialReason.InsufficientPermission;
  }

  /// <summary>
  /// Creates a new AccessDeniedException with the specified message.
  /// </summary>
  /// <param name="message">The error message.</param>
  public AccessDeniedException(string message)
    : base(message) {
    RequiredPermission = default;
    ResourceType = string.Empty;
    Reason = AccessDenialReason.InsufficientPermission;
  }

  /// <summary>
  /// Creates a new AccessDeniedException with the specified message and inner exception.
  /// </summary>
  /// <param name="message">The error message.</param>
  /// <param name="innerException">The inner exception.</param>
  public AccessDeniedException(string message, Exception innerException)
    : base(message, innerException) {
    RequiredPermission = default;
    ResourceType = string.Empty;
    Reason = AccessDenialReason.InsufficientPermission;
  }

  /// <summary>
  /// Creates a new AccessDeniedException with full details.
  /// </summary>
  /// <param name="requiredPermission">The permission that was required.</param>
  /// <param name="resourceType">Type of resource access was denied to.</param>
  /// <param name="resourceId">Optional resource identifier.</param>
  /// <param name="reason">Reason for denial.</param>
  public AccessDeniedException(
    Permission requiredPermission,
    string resourceType,
    string? resourceId = null,
    AccessDenialReason reason = AccessDenialReason.InsufficientPermission)
    : base(_formatMessage(requiredPermission, resourceType, resourceId)) {
    RequiredPermission = requiredPermission;
    ResourceType = resourceType;
    ResourceId = resourceId;
    Reason = reason;
  }

  private static string _formatMessage(Permission permission, string resourceType, string? resourceId) {
    var message = $"Access denied to {resourceType}";
    if (resourceId != null) {
      message += $" ({resourceId})";
    }
    message += $": requires {permission}";
    return message;
  }
}
