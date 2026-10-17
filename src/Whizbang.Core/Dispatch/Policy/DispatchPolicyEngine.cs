// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>
/// Resolves the policy for a call, decides, applies the developer hooks and checks the result. With no
/// hooks registered the policy's decision is returned directly, so the common path costs only the
/// policy's rules.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#extending</docs>
public sealed class DispatchPolicyEngine {
  private readonly IDispatchPolicyRegistry _registry;
  private readonly DispatchPolicyHookChain _hooks;

  /// <summary>Creates the engine.</summary>
  /// <param name="registry">Resolves the policy a caller selects.</param>
  /// <param name="hooks">The developer hooks.</param>
  public DispatchPolicyEngine(IDispatchPolicyRegistry registry, DispatchPolicyHookChain hooks) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(hooks);
    _registry = registry;
    _hooks = hooks;
  }

  /// <summary>The engine with the built-in policies and no hooks.</summary>
  public static DispatchPolicyEngine Default { get; } = new(new DispatchPolicyRegistry(), new DispatchPolicyHookChain([]));

  /// <summary>The policy to use: the caller's choice when given, otherwise the call's own.</summary>
  /// <param name="callPolicy">The policy of the call (for example <see cref="DispatchPolicies.Send"/>).</param>
  /// <param name="requested">The policy the caller selected through <see cref="DispatchOptions.Policy"/>, or null.</param>
  /// <returns>The policy to decide with.</returns>
  public DispatchPolicy Resolve(DispatchPolicy callPolicy, DispatchPolicyId? requested) =>
    requested is DispatchPolicyId id ? _registry.GetPolicy(id) : callPolicy;

  /// <summary>Decides what happens to one message.</summary>
  /// <param name="policy">The policy to decide with.</param>
  /// <param name="situation">The facts of the dispatch.</param>
  /// <returns>The decision, after the hooks, checked by the guard.</returns>
  [DebuggerStepThrough]
  public DispatchDecision Decide(DispatchPolicy policy, in DispatchSituation situation) {
    ArgumentNullException.ThrowIfNull(policy);
    var decision = policy.Decide(in situation);
    if (_hooks.IsEmpty) {
      return decision;
    }
    decision = _hooks.Apply(policy, in situation, decision);
    DispatchDecisionGuard.Validate(policy, in situation, in decision);
    return decision;
  }
}
