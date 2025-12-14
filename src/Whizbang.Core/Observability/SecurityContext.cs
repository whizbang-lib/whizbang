namespace Whizbang.Core.Observability;

/// <summary>
/// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:Constructor_SetsUserIdAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:Constructor_SetsTenantIdAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:Constructor_VariousNullCombinations_HandlesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:RecordEquality_SameValues_AreEqualAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:RecordEquality_DifferentValues_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:WithExpression_CreatesNewInstance_WithUpdatedValuesAsync</tests>
/// Security context for a message at a specific hop.
/// Contains authentication and authorization metadata that can change from hop to hop.
/// Extensible for future security requirements (roles, claims, permissions, etc.).
/// </summary>
public record SecurityContext {
  /// <summary>
  /// User identifier for authentication and authorization.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:Constructor_SetsUserIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:Constructor_VariousNullCombinations_HandlesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:RecordEquality_SameValues_AreEqualAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:RecordEquality_DifferentValues_AreNotEqualAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:WithExpression_CreatesNewInstance_WithUpdatedValuesAsync</tests>
  public string? UserId { get; init; }

  /// <summary>
  /// Tenant identifier for multi-tenancy scenarios.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:Constructor_SetsTenantIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:Constructor_VariousNullCombinations_HandlesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:RecordEquality_SameValues_AreEqualAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:RecordEquality_DifferentValues_AreNotEqualAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/SecurityContextTests.cs:WithExpression_CreatesNewInstance_WithUpdatedValuesAsync</tests>
  public string? TenantId { get; init; }
}
