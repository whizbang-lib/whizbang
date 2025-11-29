namespace Whizbang.Core.Policies;

/// <summary>
/// Records policy decisions made during message processing for debugging and time-travel capabilities.
/// This trail flows with the message and can be inspected to understand why certain policies were applied.
/// </summary>
public class PolicyDecisionTrail {
  /// <summary>
  /// List of all policy decisions made during processing.
  /// Init setter required for JSON deserialization.
  /// </summary>
  public List<PolicyDecision> Decisions { get; init; } = [];

  /// <summary>
  /// Records a policy decision with full context for debugging.
  /// </summary>
  /// <param name="policyName">Name of the policy being evaluated</param>
  /// <param name="rule">The specific rule that was evaluated</param>
  /// <param name="matched">Whether the rule matched</param>
  /// <param name="configuration">Configuration applied when rule matched</param>
  /// <param name="reason">Human-readable reason for the decision</param>
  public void RecordDecision(
      string policyName,
      string rule,
      bool matched,
      object? configuration,
      string reason
  ) {
    Decisions.Add(new PolicyDecision {
      PolicyName = policyName,
      Rule = rule,
      Matched = matched,
      Configuration = configuration,
      Reason = reason,
      Timestamp = DateTimeOffset.UtcNow
    });
  }

  /// <summary>
  /// Gets only the decisions where rules matched.
  /// </summary>
  public IEnumerable<PolicyDecision> GetMatchedRules() {
    return Decisions.Where(d => d.Matched);
  }

  /// <summary>
  /// Gets only the decisions where rules did not match.
  /// </summary>
  public IEnumerable<PolicyDecision> GetUnmatchedRules() {
    return Decisions.Where(d => !d.Matched);
  }
}

/// <summary>
/// Represents a single policy decision made during message processing.
/// </summary>
public record PolicyDecision {
  /// <summary>
  /// Name of the policy (e.g., "StreamSelection", "ExecutionStrategy")
  /// </summary>
  public required string PolicyName { get; init; }

  /// <summary>
  /// The rule that was evaluated (e.g., "Order.* → order-{id}")
  /// </summary>
  public required string Rule { get; init; }

  /// <summary>
  /// Whether this rule matched
  /// </summary>
  public required bool Matched { get; init; }

  /// <summary>
  /// Configuration applied (can be anything - stream key, executor type, etc.)
  /// </summary>
  public object? Configuration { get; init; }

  /// <summary>
  /// Human-readable reason for the decision
  /// </summary>
  public required string Reason { get; init; }

  /// <summary>
  /// When this decision was made
  /// </summary>
  public required DateTimeOffset Timestamp { get; init; }
}
