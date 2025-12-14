namespace Whizbang.Core.Policies;

/// <summary>
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
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:AddPolicy_WithNullName_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:AddPolicy_WithEmptyName_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:AddPolicy_WithWhitespaceName_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:AddPolicy_WithNullPredicate_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:AddPolicy_WithNullConfigure_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:MatchAsync_WithNullContext_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:MatchAsync_WithPredicateThrowingException_ShouldRecordFailureAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyEngineTests.cs:MatchAsync_WithPredicateThrowingException_ShouldContinueToNextPolicyAsync</tests>
/// Default policy engine implementation.
/// Evaluates policies in order and returns configuration for the first match.
/// </summary>
public class PolicyEngine : IPolicyEngine {
  private readonly List<Policy> _policies = [];

  public void AddPolicy(
    string name,
    Func<PolicyContext, bool> predicate,
    Action<PolicyConfiguration> configure
  ) {
    if (string.IsNullOrWhiteSpace(name)) {
      throw new ArgumentException("Policy name cannot be null or empty", nameof(name));
    }

    ArgumentNullException.ThrowIfNull(predicate);

    ArgumentNullException.ThrowIfNull(configure);

    _policies.Add(new Policy(name, predicate, configure));
  }

  public Task<PolicyConfiguration?> MatchAsync(PolicyContext context) {
    ArgumentNullException.ThrowIfNull(context);

    foreach (var policy in _policies) {
      bool matched;
      try {
        matched = policy.Predicate(context);
      } catch (Exception ex) {
        // Record failed evaluation
        context.Trail.RecordDecision(
          policyName: policy.Name,
          rule: "predicate",
          matched: false,
          configuration: null,
          reason: $"Evaluation failed: {ex.Message}"
        );
        continue;
      }

      if (matched) {
        // Create configuration
        var config = new PolicyConfiguration();
        policy.Configure(config);

        // Record matched decision
        context.Trail.RecordDecision(
          policyName: policy.Name,
          rule: "predicate",
          matched: true,
          configuration: config,
          reason: "Policy predicate matched"
        );

        return Task.FromResult<PolicyConfiguration?>(config);
      } else {
        // Record unmatched decision
        context.Trail.RecordDecision(
          policyName: policy.Name,
          rule: "predicate",
          matched: false,
          configuration: null,
          reason: "Policy predicate did not match"
        );
      }
    }

    // No policies matched
    return Task.FromResult<PolicyConfiguration?>(null);
  }

  /// <summary>
  /// Internal policy representation
  /// </summary>
  private record Policy(
    string Name,
    Func<PolicyContext, bool> Predicate,
    Action<PolicyConfiguration> Configure
  );
}
