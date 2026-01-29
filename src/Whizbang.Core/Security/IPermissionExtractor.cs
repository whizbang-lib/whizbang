namespace Whizbang.Core.Security;

/// <summary>
/// Interface for extracting security information from claims or other sources.
/// </summary>
/// <docs>core-concepts/security#extractors</docs>
/// <tests>Whizbang.Core.Tests/Security/SecurityOptionsTests.cs</tests>
public interface IPermissionExtractor {
  /// <summary>
  /// Extract permissions from the provided claims.
  /// </summary>
  /// <param name="claims">The claims to extract from.</param>
  /// <returns>Set of extracted permissions.</returns>
  IEnumerable<Permission> ExtractPermissions(IReadOnlyDictionary<string, string> claims);

  /// <summary>
  /// Extract role names from the provided claims.
  /// </summary>
  /// <param name="claims">The claims to extract from.</param>
  /// <returns>Set of extracted role names.</returns>
  IEnumerable<string> ExtractRoles(IReadOnlyDictionary<string, string> claims);

  /// <summary>
  /// Extract security principal IDs from the provided claims.
  /// </summary>
  /// <param name="claims">The claims to extract from.</param>
  /// <returns>Set of extracted security principal IDs.</returns>
  IEnumerable<SecurityPrincipalId> ExtractSecurityPrincipals(IReadOnlyDictionary<string, string> claims);
}

/// <summary>
/// Extracts permissions from a specific claim type.
/// </summary>
internal sealed class ClaimPermissionExtractor : IPermissionExtractor {
  private readonly string _claimType;

  public ClaimPermissionExtractor(string claimType) => _claimType = claimType;

  public IEnumerable<Permission> ExtractPermissions(IReadOnlyDictionary<string, string> claims) {
    if (claims.TryGetValue(_claimType, out var value) && !string.IsNullOrWhiteSpace(value)) {
      foreach (var permission in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
        yield return new Permission(permission);
      }
    }
  }

  public IEnumerable<string> ExtractRoles(IReadOnlyDictionary<string, string> claims) =>
    Enumerable.Empty<string>();

  public IEnumerable<SecurityPrincipalId> ExtractSecurityPrincipals(IReadOnlyDictionary<string, string> claims) =>
    Enumerable.Empty<SecurityPrincipalId>();
}

/// <summary>
/// Extracts roles from a specific claim type.
/// </summary>
internal sealed class ClaimRoleExtractor : IPermissionExtractor {
  private readonly string _claimType;

  public ClaimRoleExtractor(string claimType) => _claimType = claimType;

  public IEnumerable<Permission> ExtractPermissions(IReadOnlyDictionary<string, string> claims) =>
    Enumerable.Empty<Permission>();

  public IEnumerable<string> ExtractRoles(IReadOnlyDictionary<string, string> claims) {
    if (claims.TryGetValue(_claimType, out var value) && !string.IsNullOrWhiteSpace(value)) {
      foreach (var role in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
        yield return role;
      }
    }
  }

  public IEnumerable<SecurityPrincipalId> ExtractSecurityPrincipals(IReadOnlyDictionary<string, string> claims) =>
    Enumerable.Empty<SecurityPrincipalId>();
}

/// <summary>
/// Extracts security principals from a specific claim type.
/// </summary>
internal sealed class ClaimSecurityPrincipalExtractor : IPermissionExtractor {
  private readonly string _claimType;

  public ClaimSecurityPrincipalExtractor(string claimType) => _claimType = claimType;

  public IEnumerable<Permission> ExtractPermissions(IReadOnlyDictionary<string, string> claims) =>
    Enumerable.Empty<Permission>();

  public IEnumerable<string> ExtractRoles(IReadOnlyDictionary<string, string> claims) =>
    Enumerable.Empty<string>();

  public IEnumerable<SecurityPrincipalId> ExtractSecurityPrincipals(IReadOnlyDictionary<string, string> claims) {
    if (claims.TryGetValue(_claimType, out var value) && !string.IsNullOrWhiteSpace(value)) {
      foreach (var principal in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
        yield return new SecurityPrincipalId(principal);
      }
    }
  }
}
