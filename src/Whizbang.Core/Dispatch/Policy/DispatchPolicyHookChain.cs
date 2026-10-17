// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>The registered <see cref="IDispatchPolicyHook"/>s, sorted once by order.</summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#hooks</docs>
public sealed class DispatchPolicyHookChain {
  private readonly IDispatchPolicyHook[] _hooks;

  /// <summary>Creates the chain from the registered hooks, in any order.</summary>
  /// <param name="hooks">The registered hooks.</param>
  public DispatchPolicyHookChain(IEnumerable<IDispatchPolicyHook> hooks) {
    ArgumentNullException.ThrowIfNull(hooks);
    _hooks = [.. hooks.OrderBy(h => h.Order)];
  }

  /// <summary>True when no hook is registered, so the policy's decision is used as is.</summary>
  public bool IsEmpty => _hooks.Length == 0;

  /// <summary>
  /// Runs every hook over the proposal, each seeing the previous result. A decision a hook changed is
  /// recorded with <see cref="DispatchReason.Hook"/>.
  /// </summary>
  /// <param name="policy">The policy that made the proposal.</param>
  /// <param name="situation">The facts of the dispatch.</param>
  /// <param name="proposed">The policy's decision.</param>
  /// <returns>The decision after every hook.</returns>
  [DebuggerStepThrough]
  public DispatchDecision Apply(DispatchPolicy policy, in DispatchSituation situation, DispatchDecision proposed) {
    var decision = proposed;
    foreach (var hook in _hooks) {
      var next = hook.Decide(policy, in situation, in decision);
      decision = next == decision ? decision : next with { Reason = DispatchReason.Hook };
    }
    return decision;
  }
}
