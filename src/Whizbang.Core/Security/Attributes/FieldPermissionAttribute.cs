// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Security.Attributes;

/// <summary>
/// Restricts field visibility based on caller permissions.
/// When the caller lacks the required permission, the field value is masked.
/// </summary>
/// <remarks>
/// <para>
/// The GraphQL and REST lens transports enforce it on every response: a caller whose scope does not hold
/// <see cref="Permission"/> sees the value <see cref="FieldPermissionMasking"/> produces for
/// <see cref="Masking"/>, and a caller with no scope is never permitted. A protected member cannot be used
/// to filter or sort a lens query, since a filter or an ordering would reveal the value it hides.
/// </para>
/// <para>
/// The placeholders apply to string members; a member of any other type is hidden under every strategy.
/// Code that reads a lens directly gets the stored values and applies <see cref="FieldPermissionMasking"/>
/// itself.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/security#column-level-security</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/SecurityAttributeTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/FieldPermissionMaskingTests.cs</tests>
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
/// <remarks>
/// The placeholders apply to string members. A member of any other type is hidden under every strategy,
/// because a placeholder string cannot stand in for its value. See <see cref="FieldPermissionMasking"/>.
/// </remarks>
/// <docs>fundamentals/security/security#masking-strategies</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/SecurityAttributeTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/SecurityAttributeTests.cs:MaskingStrategy_AllValues_AreDistinctAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/SecurityAttributeTests.cs:FieldPermissionAttribute_Constructor_WithMaskingStrategy_SetsMaskingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/SecurityAttributeTests.cs:FieldPermissionAttribute_Constructor_DefaultMaskingIsHideAsync</tests>
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
  /// A value of four characters or fewer is fully masked ("****").
  /// </summary>
  Partial = 2,

  /// <summary>
  /// Return "[REDACTED]" placeholder.
  /// </summary>
  Redact = 3
}
