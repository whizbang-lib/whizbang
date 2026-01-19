namespace Whizbang.Core.Policies;

/// <summary>
/// Policy engine that matches messages to policies and returns configuration.
/// Evaluates policies in order until a match is found.
/// </summary>
/// <docs>infrastructure/policies</docs>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldMatchSinglePolicyAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldMatchFirstMatchingPolicyAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldReturnNullWhenNoPolicyMatchesAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldRecordDecisionInTrailAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldRecordUnmatchedPoliciesInTrailAsync</tests>
public interface IPolicyEngine {
  /// <summary>
  /// Adds a policy to the engine.
  /// Policies are evaluated in the order they are added.
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldMatchSinglePolicyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldMatchFirstMatchingPolicyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:AddPolicy_WithNullName_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:AddPolicy_WithEmptyName_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:AddPolicy_WithWhitespaceName_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:AddPolicy_WithNullPredicate_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:AddPolicy_WithNullConfigure_ShouldThrowAsync</tests>
  void AddPolicy(
    string name,
    Func<PolicyContext, bool> predicate,
    Action<PolicyConfiguration> configure
  );

  /// <summary>
  /// Matches a message against registered policies.
  /// Returns the configuration for the first matching policy, or null if no match.
  /// Records all policy evaluations in the context's decision trail.
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldMatchSinglePolicyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldMatchFirstMatchingPolicyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldReturnNullWhenNoPolicyMatchesAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldRecordDecisionInTrailAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyEngine_ShouldRecordUnmatchedPoliciesInTrailAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyConfiguration_ShouldSupportTopicAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyConfiguration_ShouldSupportStreamKeyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyConfiguration_ShouldSupportExecutionStrategyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyConfiguration_ShouldSupportPartitionRouterAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyConfiguration_ShouldSupportSequenceProviderAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyConfiguration_ShouldSupportPartitionCountAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:PolicyConfiguration_ShouldSupportConcurrencyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:MatchAsync_WithNullContext_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:MatchAsync_WithPredicateThrowingException_ShouldRecordFailureAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:MatchAsync_WithPredicateThrowingException_ShouldContinueToNextPolicyAsync</tests>
  Task<PolicyConfiguration?> MatchAsync(PolicyContext context);
}
