namespace Whizbang.Core.Policies;

/// <summary>
/// Default policy engine implementation.
/// Evaluates policies in order and returns configuration for the first match.
/// </summary>
public class PolicyEngine : IPolicyEngine {
  private readonly List<Policy> _policies = new();

  public void AddPolicy(
    string name,
    Func<PolicyContext, bool> predicate,
    Action<PolicyConfiguration> configure
  ) {
    if (string.IsNullOrWhiteSpace(name)) {
      throw new ArgumentException("Policy name cannot be null or empty", nameof(name));
    }

    if (predicate == null) {
      throw new ArgumentNullException(nameof(predicate));
    }

    if (configure == null) {
      throw new ArgumentNullException(nameof(configure));
    }

    _policies.Add(new Policy(name, predicate, configure));
  }

  public Task<PolicyConfiguration?> MatchAsync(PolicyContext context) {
    if (context == null) {
      throw new ArgumentNullException(nameof(context));
    }

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
