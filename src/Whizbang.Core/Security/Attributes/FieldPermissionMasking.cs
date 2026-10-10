// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Security.Attributes;

/// <summary>
/// Decides whether a caller may see a member marked <see cref="FieldPermissionAttribute"/>, and what a caller
/// without the permission sees instead. The GraphQL and REST lens transports apply it to every response; call
/// it directly when your own code returns a model read from a lens.
/// </summary>
/// <remarks>
/// <para>
/// A caller is permitted when its scope holds the attribute's permission. A caller with no scope holds no
/// permissions, so the check fails closed.
/// </para>
/// <para>
/// The placeholders apply to string members only. A member of any other type (a number, a date, a nested
/// object, a list) is hidden under every strategy, because a placeholder string cannot stand in for its value.
/// A null string under <see cref="MaskingStrategy.Mask"/>, <see cref="MaskingStrategy.Partial"/> or
/// <see cref="MaskingStrategy.Redact"/> still yields the placeholder, so whether a value is present is not
/// shown either. A strategy the framework does not define hides the value.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/security#masking-strategies</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/FieldPermissionMaskingTests.cs</tests>
public static class FieldPermissionMasking {
  /// <summary>The placeholder <see cref="MaskingStrategy.Mask"/> returns.</summary>
  public const string MASKED_VALUE = "****";

  /// <summary>The placeholder <see cref="MaskingStrategy.Redact"/> returns.</summary>
  public const string REDACTED_VALUE = "[REDACTED]";

  /// <summary>
  /// How many trailing characters <see cref="MaskingStrategy.Partial"/> shows. A value no longer than this is
  /// masked entirely, since showing its last characters would show all of it.
  /// </summary>
  public const int PARTIAL_VISIBLE_CHARACTERS = 4;

  /// <summary>
  /// Whether the caller may see the member.
  /// </summary>
  /// <param name="scope">The caller's scope, or <see langword="null"/> when there is none.</param>
  /// <param name="attribute">The member's field permission.</param>
  /// <returns><see langword="true"/> when the scope holds the permission; <see langword="false"/> otherwise, including when there is no scope.</returns>
  public static bool IsPermitted(IScopeContext? scope, FieldPermissionAttribute attribute) {
    ArgumentNullException.ThrowIfNull(attribute);
    return scope?.HasPermission(attribute.Permission) == true;
  }

  /// <summary>
  /// What a caller without the permission sees for a string member.
  /// </summary>
  /// <param name="value">The member's value.</param>
  /// <param name="strategy">The member's masking strategy.</param>
  /// <returns>The placeholder for the strategy, or <see langword="null"/> when the value is hidden.</returns>
  public static string? MaskString(string? value, MaskingStrategy strategy) => strategy switch {
    MaskingStrategy.Mask => MASKED_VALUE,
    MaskingStrategy.Partial => value is { Length: > PARTIAL_VISIBLE_CHARACTERS }
      ? MASKED_VALUE + value[^PARTIAL_VISIBLE_CHARACTERS..]
      : MASKED_VALUE,
    MaskingStrategy.Redact => REDACTED_VALUE,
    _ => null,
  };

  /// <summary>
  /// What a caller without the permission sees for a member of any type.
  /// </summary>
  /// <param name="value">The member's value.</param>
  /// <param name="strategy">The member's masking strategy.</param>
  /// <param name="isStringMember">Whether the member is declared as a string.</param>
  /// <returns>The string placeholder for a string member; <see langword="null"/> for any other member.</returns>
  public static object? MaskValue(object? value, MaskingStrategy strategy, bool isStringMember) =>
    isStringMember ? MaskString(value as string, strategy) : null;

  /// <summary>
  /// Whether a caller without the permission gets no value at all, rather than a placeholder.
  /// </summary>
  /// <param name="strategy">The member's masking strategy.</param>
  /// <param name="isStringMember">Whether the member is declared as a string.</param>
  /// <returns><see langword="true"/> for a non-string member, and for a string member under <see cref="MaskingStrategy.Hide"/> or an undefined strategy.</returns>
  public static bool IsWithheld(MaskingStrategy strategy, bool isStringMember) =>
    !isStringMember || strategy is not (MaskingStrategy.Mask or MaskingStrategy.Partial or MaskingStrategy.Redact);
}
