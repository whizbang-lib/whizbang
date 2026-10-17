// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>
/// The shared rules every dispatch policy is built from. Each rule exists once, so every policy that
/// needs it applies it the same way.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#rules</docs>
public static class DispatchRules {
  /// <summary>
  /// The caller's route decides: local handlers run only when it includes local dispatch, the outbox
  /// is written only when it includes the outbox, and an event is stored without being sent when it
  /// includes the event store but not the outbox.
  /// </summary>
  public static readonly DispatchRule RouteDecides = _routeDecides;

  /// <summary>A local call never writes the outbox or the event store.</summary>
  public static readonly DispatchRule LocalCallNeverSendsOut = _localCallNeverSendsOut;

  /// <summary>A command in one of this service's owned namespaces never leaves the service, on any route.</summary>
  public static readonly DispatchRule OwnedCommandStaysLocal = _ownedCommandStaysLocal;

  /// <summary>A framework system command reaches every service, the sender included, on any call that is not local only.</summary>
  public static readonly DispatchRule SystemCommandBroadcasts = _systemCommandBroadcasts;

  /// <summary>A command routed to the event store only is refused: commands are not stored.</summary>
  public static readonly DispatchRule CommandCannotBeStoreOnly = _commandCannotBeStoreOnly;

  /// <summary>
  /// A handler is required when the policy expects one, and for an owned command, which no other
  /// service handles.
  /// </summary>
  public static readonly DispatchRule HandlerRequired = _handlerRequired;

  /// <summary>
  /// A message scheduled for later is handled by this service when its outbox copy comes back; a
  /// scheduled message that never leaves the service is refused, since nothing could deliver it.
  /// </summary>
  public static readonly DispatchRule ScheduledDefersBoth = _scheduledDefersBoth;

  /// <summary>
  /// With no outbox: a message this service handles now simply is not sent on; otherwise the
  /// policy's <see cref="NoOutboxBehavior"/> applies.
  /// </summary>
  public static readonly DispatchRule NoOutbox = _noOutbox;

  private static DispatchDecision _routeDecides(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
    current;

  private static DispatchDecision _localCallNeverSendsOut(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
    current;

  private static DispatchDecision _ownedCommandStaysLocal(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
    current;

  private static DispatchDecision _systemCommandBroadcasts(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
    current;

  private static DispatchDecision _commandCannotBeStoreOnly(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
    current;

  private static DispatchDecision _handlerRequired(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
    current;

  private static DispatchDecision _scheduledDefersBoth(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
    current;

  private static DispatchDecision _noOutbox(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
    current;
}
