// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>
/// The built-in dispatch policies, one per kind of dispatch call. Their decisions are the published
/// decision table.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#policies</docs>
public static class DispatchPolicies {
  /// <summary><c>SendAsync</c>: a command runs its local handler when this service has one and goes to the outbox unless it is owned.</summary>
  public static readonly DispatchPolicy Send = new(
    new DispatchPolicyId(1), nameof(Send), DispatchExpectations.None, NoOutboxBehavior.Throw,
    DispatchRules.BehaviorsAreConsistent,
    DispatchRules.RouteDecides,
    DispatchRules.OwnedCommandStaysLocal,
    DispatchRules.SystemCommandBroadcasts,
    DispatchRules.CommandCannotBeStoreOnly,
    DispatchRules.HandlerRequired,
    DispatchRules.ScheduledDefersBoth,
    DispatchRules.ResultRequiresLocalRun,
    DispatchRules.NoOutbox);

  /// <summary><c>SendManyAsync</c>: <see cref="Send"/> for each message, with the batch checked first.</summary>
  public static readonly DispatchPolicy SendMany = new(
    new DispatchPolicyId(2), nameof(SendMany), DispatchExpectations.BatchValidatesFirst, NoOutboxBehavior.Throw,
    DispatchRules.BehaviorsAreConsistent,
    DispatchRules.RouteDecides,
    DispatchRules.OwnedCommandStaysLocal,
    DispatchRules.SystemCommandBroadcasts,
    DispatchRules.CommandCannotBeStoreOnly,
    DispatchRules.HandlerRequired,
    DispatchRules.ScheduledDefersBoth,
    DispatchRules.ResultRequiresLocalRun,
    DispatchRules.NoOutbox);

  /// <summary><c>LocalInvokeAsync</c>: local only, returns the handler's result, fails without a handler.</summary>
  public static readonly DispatchPolicy LocalInvoke = new(
    new DispatchPolicyId(3), nameof(LocalInvoke),
    DispatchExpectations.LocalOnly | DispatchExpectations.ReturnsHandlerResult | DispatchExpectations.ThrowWhenNoHandler,
    NoOutboxBehavior.Throw,
    DispatchRules.BehaviorsAreConsistent,
    DispatchRules.LocalCallNeverSendsOut,
    DispatchRules.HandlerRequired,
    DispatchRules.ScheduledDefersBoth,
    DispatchRules.ResultRequiresLocalRun);

  /// <summary><c>LocalSendManyAsync</c>: local only for each message, with the batch checked first.</summary>
  public static readonly DispatchPolicy LocalSendMany = new(
    new DispatchPolicyId(4), nameof(LocalSendMany),
    DispatchExpectations.LocalOnly | DispatchExpectations.ThrowWhenNoHandler | DispatchExpectations.BatchValidatesFirst,
    NoOutboxBehavior.Throw,
    DispatchRules.BehaviorsAreConsistent,
    DispatchRules.LocalCallNeverSendsOut,
    DispatchRules.HandlerRequired,
    DispatchRules.ScheduledDefersBoth,
    DispatchRules.ResultRequiresLocalRun);

  /// <summary><c>PublishAsync</c>: every local receptor runs, and the message goes to the outbox.</summary>
  public static readonly DispatchPolicy Publish = new(
    new DispatchPolicyId(5), nameof(Publish), DispatchExpectations.FanOutAllReceptors, NoOutboxBehavior.DeferOrLog,
    DispatchRules.BehaviorsAreConsistent,
    DispatchRules.RouteDecides,
    DispatchRules.OwnedCommandStaysLocal,
    DispatchRules.SystemCommandBroadcasts,
    DispatchRules.CommandCannotBeStoreOnly,
    DispatchRules.HandlerRequired,
    DispatchRules.ScheduledDefersBoth,
    DispatchRules.ResultRequiresLocalRun,
    DispatchRules.NoOutbox);

  /// <summary><c>PublishManyAsync</c>: <see cref="Publish"/> for each message, with the batch checked first.</summary>
  public static readonly DispatchPolicy PublishMany = new(
    new DispatchPolicyId(6), nameof(PublishMany),
    DispatchExpectations.FanOutAllReceptors | DispatchExpectations.BatchValidatesFirst, NoOutboxBehavior.DeferOrLog,
    DispatchRules.BehaviorsAreConsistent,
    DispatchRules.RouteDecides,
    DispatchRules.OwnedCommandStaysLocal,
    DispatchRules.SystemCommandBroadcasts,
    DispatchRules.CommandCannotBeStoreOnly,
    DispatchRules.HandlerRequired,
    DispatchRules.ScheduledDefersBoth,
    DispatchRules.ResultRequiresLocalRun,
    DispatchRules.NoOutbox);

  /// <summary>A message returned by a handler, or cascaded explicitly: its route decides, and owned commands stay local.</summary>
  public static readonly DispatchPolicy Cascade = new(
    new DispatchPolicyId(7), nameof(Cascade), DispatchExpectations.FanOutAllReceptors, NoOutboxBehavior.DeferOrLog,
    DispatchRules.BehaviorsAreConsistent,
    DispatchRules.RouteDecides,
    DispatchRules.OwnedCommandStaysLocal,
    DispatchRules.SystemCommandBroadcasts,
    DispatchRules.CommandCannotBeStoreOnly,
    DispatchRules.HandlerRequired,
    DispatchRules.ScheduledDefersBoth,
    DispatchRules.ResultRequiresLocalRun,
    DispatchRules.NoOutbox);

  /// <summary>Every built-in policy, in id order.</summary>
  public static IReadOnlyList<DispatchPolicy> All { get; } = [Send, SendMany, LocalInvoke, LocalSendMany, Publish, PublishMany, Cascade];
}
