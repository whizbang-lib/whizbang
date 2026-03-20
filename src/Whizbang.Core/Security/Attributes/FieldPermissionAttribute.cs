#pragma warning disable S3604 // Primary constructor field/property initializers are intentional

namespace Whizbang.Core.Security.Attributes;

/// <summary>
/// Restricts field visibility based on caller permissions.
/// When the caller lacks the required permission, the field value is masked.
/// </summary>
/// <docs>fundamentals/security/security#column-level-security</docs>
/// <tests>Whizbang.Core.Tests/Security/SecurityAttributeTests.cs</tests>
/// <example>
/// public class Customer {
///   public string Name { get; init; }
///
///   [FieldPermission("pii:view")]
///   public string Email { get; init; }
///
///   [FieldPermission("pii:view", MaskingStrategy.Partial)]
///   public string SSN { get; init; }  // Returns "****1234"
/// }
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class FieldPermissionAttribute(string permission, MaskingStrategy masking = MaskingStrategy.Hide) : Attribute {
  /// <summary>
  /// The permission required to view this field.
  /// </summary>
  public Permission Permission { get; } = new Permission(permission);

  /// <summary>
  /// The masking strategy to apply when permission is not granted.
  /// </summary>
  public MaskingStrategy Masking { get; } = masking;
}

/// <summary>
/// Strategy for masking restricted fields when permission is not granted.
/// </summary>
/// <docs>fundamentals/security/security#masking-strategies</docs>
/// <tests>Whizbang.Core.Tests/Security/SecurityAttributeTests.cs</tests>
public enum MaskingStrategy {
  /// <summary>
  /// Return null/default value.
  /// </summary>
  Hide = 0,

  /// <summary>
  /// Return "****" placeholder.
  /// </summary>
  Mask = 1,

  /// <summary>
  /// Return partial value like "****1234" (last 4 characters visible).
  /// </summary>
  Partial = 2,

  /// <summary>
  /// Return "[REDACTED]" placeholder.
  /// </summary>
  Redact = 3
}
