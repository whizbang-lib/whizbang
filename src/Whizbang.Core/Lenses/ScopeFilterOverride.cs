namespace Whizbang.Core.Lenses;

/// <summary>
/// Override values for <see cref="ILensQuery{TModel}.ScopeOverride"/>.
/// Allows specifying explicit scope values instead of using ambient context.
/// Useful for system processes that need to query as a specific tenant/user.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#scope-override</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopeFilterOverrideTests.cs</tests>
public readonly record struct ScopeFilterOverride {
  /// <summary>
  /// Override tenant ID. When set, used instead of the ambient TenantId.
  /// </summary>
  public string? TenantId { get; init; }

  /// <summary>
  /// Override user ID. When set, used instead of the ambient UserId.
  /// </summary>
  public string? UserId { get; init; }

  /// <summary>
  /// Override organization ID. When set, used instead of the ambient OrganizationId.
  /// </summary>
  public string? OrganizationId { get; init; }

  /// <summary>
  /// Override customer ID. When set, used instead of the ambient CustomerId.
  /// </summary>
  public string? CustomerId { get; init; }
}
