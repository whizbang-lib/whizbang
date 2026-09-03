namespace Whizbang.Transports.HotChocolate.Middleware;

/// <summary>
/// Strategy for resolving multi-valued claims when more than one claim name is configured
/// (e.g., <see cref="WhizbangScopeOptions.PermissionsClaimTypes"/>).
/// </summary>
/// <docs>apis/graphql/scoping#claim-aggregation</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/WhizbangScopeOptionsSymmetryTests.cs:Defaults_PermissionsAggregation_IsFirstMatchAsync</tests>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/WhizbangScopeOptionsSymmetryTests.cs:Permissions_FirstMatch_OnlyReadsFirstClaimWithValuesAsync</tests>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/WhizbangScopeOptionsSymmetryTests.cs:Permissions_Aggregate_UnionsAcrossAllClaimsAsync</tests>
public enum ClaimAggregation {
  /// <summary>
  /// Use the first claim name (in priority order) that yields any value. Subsequent claim
  /// names are ignored once a match is found. This is the default and matches the legacy
  /// "single source" behavior.
  /// </summary>
  FirstMatch,
  /// <summary>
  /// Union the values from every configured claim name and deduplicate. Useful when a
  /// tenant straddles two identity providers that emit the same logical concept under
  /// different claim names (e.g., <c>permissions</c> on one IdP and <c>perms</c> on another).
  /// </summary>
  Aggregate,
}
