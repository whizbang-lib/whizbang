// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>
/// A developer's adjustment to dispatch decisions. Hooks run after the policy, in <see cref="Order"/>,
/// each seeing the previous result. A hook with no opinion returns <c>proposed</c> unchanged.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#hooks</docs>
public interface IDispatchPolicyHook {
  /// <summary>The position of this hook; lower runs first.</summary>
  int Order { get; }

  /// <summary>Returns the decision to use, given the policy's (or an earlier hook's) proposal.</summary>
  /// <param name="policy">The policy that made the proposal.</param>
  /// <param name="situation">The facts of the dispatch.</param>
  /// <param name="proposed">The decision so far.</param>
  /// <returns>The decision to use.</returns>
  DispatchDecision Decide(DispatchPolicy policy, in DispatchSituation situation, in DispatchDecision proposed);
}
