namespace Whizbang.Core.Policies;

/// <summary>
/// Policy engine that matches messages to policies and returns configuration.
/// Evaluates policies in order until a match is found.
/// </summary>
/// <docs>infrastructure/policies</docs>
public interface IPolicyEngine {
  /// <summary>
  /// Adds a policy to the engine.
  /// Policies are evaluated in the order they are added.
  /// </summary>
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
  Task<PolicyConfiguration?> MatchAsync(PolicyContext context);
}
