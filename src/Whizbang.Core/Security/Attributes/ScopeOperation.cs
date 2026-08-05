namespace Whizbang.Core.Security.Attributes;

/// <summary>
/// The operation kind a <see cref="RequirePermissionAttribute"/> applies to. Lets the
/// same permission key serve both read and write contexts on a single model without
/// splitting attributes — the type-level attribute on a model is checked when the field
/// is part of a Query (Read) and a method-level attribute can require Write on a Mutation.
/// </summary>
/// <docs>fundamentals/security/security#permission-based-rls</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/SecurityAttributeTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/SecurityAttributeTests.cs:RequirePermissionAttribute_Operation_DefaultsToAnyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/SecurityAttributeTests.cs:RequirePermissionAttribute_Operation_AcceptsReadAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/SecurityAttributeTests.cs:RequirePermissionAttribute_Operation_AcceptsWriteAsync</tests>
public enum ScopeOperation {
  /// <summary>
  /// Applies to any operation kind. Default — preserves legacy class-only behavior where
  /// the attribute gates row-level access without distinguishing read from write.
  /// </summary>
  Any = 0,
  /// <summary>Applies only when the resolver is part of a Query.</summary>
  Read = 1,
  /// <summary>Applies only when the resolver is part of a Mutation.</summary>
  Write = 2,
}
