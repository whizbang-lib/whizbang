// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>
/// Refuses a decision the dispatcher cannot carry out. Every built-in decision passes; the guard exists
/// for developer hooks, which may change ownership or broadcast but not ask for the impossible.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#guardrails</docs>
public static class DispatchDecisionGuard {
  /// <summary>Throws <see cref="InvalidDispatchPolicyException"/> when the decision is impossible.</summary>
  /// <param name="policy">The policy the decision was made under.</param>
  /// <param name="situation">The facts of the dispatch.</param>
  /// <param name="decision">The decision to check.</param>
  public static void Validate(DispatchPolicy policy, in DispatchSituation situation, in DispatchDecision decision) {
    ArgumentNullException.ThrowIfNull(policy);
    if (decision.IsRejected) {
      return;
    }
    var problem = _findProblem(policy, in situation, in decision);
    if (problem is not null) {
      throw new InvalidDispatchPolicyException(
        $"The {policy.Name} dispatch policy decided something the dispatcher cannot do for {situation.MessageType.Name}: {problem}.");
    }
  }

  private static string? _findProblem(DispatchPolicy policy, in DispatchSituation situation, in DispatchDecision decision) {
    if (decision.Local != LocalRun.None && !situation.HasLocalHandler) {
      return "it runs local handlers, but this service has no local handler";
    }
    if (policy.Expects(DispatchExpectations.LocalOnly) && (decision.WriteOutbox || decision.WriteEventStoreOnly)) {
      return "a local-only call cannot write the outbox or the event store";
    }
    if (situation.ResultRequired && decision.Local != LocalRun.Now) {
      return "the caller needs the handler's result, but the handler does not run during the call";
    }
    if (situation.IsCommand && decision.WriteEventStoreOnly) {
      return "a command cannot be stored without being sent";
    }
    if (decision.Local == LocalRun.AtScheduledTime && !decision.WriteOutbox) {
      return "local handling is deferred, but no outbox copy is written to bring it back";
    }
    if (decision.DeferUntil is not null && !decision.WriteOutbox) {
      return "a delivery time is set, but no outbox copy is written";
    }
    return null;
  }
}
