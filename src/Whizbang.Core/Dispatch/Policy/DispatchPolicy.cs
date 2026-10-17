// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>What a kind of dispatch call promises its caller, independent of the message.</summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#policies</docs>
[Flags]
public enum DispatchExpectations : byte {
  /// <summary>No special expectation.</summary>
  None = 0,

  /// <summary>The call is local only: the message never leaves this service.</summary>
  LocalOnly = 1,

  /// <summary>The call returns the handler's result to the caller.</summary>
  ReturnsHandlerResult = 2,

  /// <summary>Every local receptor for the message runs, not only the first one.</summary>
  FanOutAllReceptors = 4,

  /// <summary>The call fails when this service has no handler for the message.</summary>
  ThrowWhenNoHandler = 8,

  /// <summary>A batch is checked as a whole before any message in it runs.</summary>
  BatchValidatesFirst = 16,
}

/// <summary>What a call does when the message has to reach an outbox and this service has none.</summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#policies</docs>
public enum NoOutboxBehavior : byte {
  /// <summary>Refuse the dispatch.</summary>
  Throw = 0,

  /// <summary>Hand the message to the deferred outbox channel when one is registered, otherwise log it.</summary>
  DeferOrLog = 1,
}

/// <summary>
/// One shared rule of a dispatch policy: it takes the decision so far and returns it, changed or not.
/// </summary>
/// <param name="situation">The facts of the dispatch.</param>
/// <param name="policy">The policy the rule runs in, for its expectations.</param>
/// <param name="current">The decision so far.</param>
/// <returns>The decision after this rule.</returns>
/// <docs>fundamentals/dispatcher/dispatch-policy#rules</docs>
public delegate DispatchDecision DispatchRule(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current);

/// <summary>
/// Identifies a dispatch policy. Built-in policies use the values below 1000; a developer's own
/// policies use 1000 and above.
/// </summary>
/// <param name="Value">The policy's key.</param>
/// <docs>fundamentals/dispatcher/dispatch-policy#custom-policies</docs>
public readonly record struct DispatchPolicyId(int Value) {
  /// <summary>The first value available to a developer's own policies.</summary>
  public const int FIRST_CUSTOM_VALUE = 1000;

  /// <summary>True for a developer's own policy.</summary>
  public bool IsCustom => Value >= FIRST_CUSTOM_VALUE;
}

/// <summary>
/// A named set of shared rules for one kind of dispatch call. A call site picks a policy and
/// describes its situation; the policy decides what happens.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#policies</docs>
public sealed class DispatchPolicy {
  private readonly DispatchRule[] _rules;

  /// <summary>Creates a policy from shared rules, run in the order given.</summary>
  /// <param name="id">The policy's key.</param>
  /// <param name="name">The policy's name, as the decision table and logs show it.</param>
  /// <param name="expectations">What the call promises its caller.</param>
  /// <param name="noOutbox">What the call does when it needs an outbox and has none.</param>
  /// <param name="rules">The shared rules, in order.</param>
  public DispatchPolicy(DispatchPolicyId id, string name, DispatchExpectations expectations, NoOutboxBehavior noOutbox, params DispatchRule[] rules) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentNullException.ThrowIfNull(rules);
    Id = id;
    Name = name;
    Expectations = expectations;
    NoOutbox = noOutbox;
    _rules = [.. rules];
  }

  /// <summary>The policy's key.</summary>
  public DispatchPolicyId Id { get; }

  /// <summary>The policy's name.</summary>
  public string Name { get; }

  /// <summary>What the call promises its caller.</summary>
  public DispatchExpectations Expectations { get; }

  /// <summary>What the call does when it needs an outbox and has none.</summary>
  public NoOutboxBehavior NoOutbox { get; }

  /// <summary>The shared rules, in the order they run.</summary>
  public IReadOnlyList<DispatchRule> Rules => _rules;

  /// <summary>True when the policy has the given expectation.</summary>
  public bool Expects(DispatchExpectations expectation) => (Expectations & expectation) == expectation;

  /// <summary>
  /// Decides what happens to one message: starts from the baseline (local handlers when present,
  /// plus the outbox) and runs each rule in order. A rejection ends the run.
  /// </summary>
  /// <param name="situation">The facts of the dispatch.</param>
  /// <returns>The decision.</returns>
  [DebuggerStepThrough]
  public DispatchDecision Decide(in DispatchSituation situation) {
    var decision = new DispatchDecision(
      situation.HasLocalHandler ? LocalRun.Now : LocalRun.None,
      WriteOutbox: true,
      WriteEventStoreOnly: false,
      DeferUntil: null,
      DispatchRejection.None,
      DispatchReason.Default);
    foreach (var rule in _rules) {
      decision = rule(in situation, this, in decision);
      if (decision.IsRejected) {
        return decision;
      }
    }
    return decision;
  }

  /// <inheritdoc />
  public override string ToString() => Name;
}
