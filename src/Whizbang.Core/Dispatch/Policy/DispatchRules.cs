// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>
/// The shared rules every dispatch policy is built from. Each rule exists once, so every policy that
/// needs it applies it the same way.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#rules</docs>
public static class DispatchRules {
  /// <summary>The caller cannot ask both to fail and to continue when there is no local handler.</summary>
  public static readonly DispatchRule BehaviorsAreConsistent = _behaviorsAreConsistent;

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
  /// A handler is required when the policy expects one, for an owned command (no other service handles
  /// it), and for a command the route keeps in this service. The caller can override either way:
  /// <see cref="DispatchBehaviors.ThrowWhenNoHandler"/> fails on any missing handler, and
  /// <see cref="DispatchBehaviors.ContinueWhenNoHandler"/> goes ahead unless the caller needs the
  /// handler's result.
  /// </summary>
  public static readonly DispatchRule HandlerRequired = _handlerRequired;

  /// <summary>
  /// A message scheduled for later is handled by this service when its outbox copy comes back. A
  /// scheduled message that never leaves the service is refused, since nothing could deliver it, and
  /// so is one with no outbox to hold it, unless the caller sets
  /// <see cref="DispatchBehaviors.AllowScheduleWithoutOutbox"/>.
  /// </summary>
  public static readonly DispatchRule ScheduledDefersBoth = _scheduledDefersBoth;

  /// <summary>
  /// A caller that needs the handler's result (a typed local invoke) gets it only when the handler runs
  /// during the call; any other decision is refused.
  /// </summary>
  public static readonly DispatchRule ResultRequiresLocalRun = _resultRequiresLocalRun;

  /// <summary>
  /// With no outbox: a policy that defers or logs keeps the write for that fallback; otherwise a
  /// message this service handles now simply is not sent on, and one it does not handle is refused.
  /// </summary>
  public static readonly DispatchRule NoOutbox = _noOutbox;

  private static DispatchDecision _behaviorsAreConsistent(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
    situation.Has(DispatchBehaviors.ThrowWhenNoHandler | DispatchBehaviors.ContinueWhenNoHandler)
      ? DispatchDecision.Rejected(DispatchRejection.ConflictingBehaviors, DispatchReason.HandlerRequired)
      : current;

  private static DispatchDecision _routeDecides(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) {
    if (situation.Route is not DispatchModes route) {
      return current;
    }
    if (route == DispatchModes.None) {
      return DispatchDecision.Rejected(DispatchRejection.NoRoute, DispatchReason.RouteDecides);
    }
    var local = (route & DispatchModes.LocalDispatch) != 0;
    var outbox = (route & DispatchModes.Outbox) != 0;
    var store = (route & DispatchModes.EventStore) != 0;
    // A command is never stored; it is store-only only when the route sends it nowhere else, which
    // CommandCannotBeStoreOnly then refuses.
    var storeOnly = store && !outbox && (!situation.IsCommand || !local);
    return current with {
      Local = local ? current.Local : LocalRun.None,
      WriteOutbox = current.WriteOutbox && outbox,
      WriteEventStoreOnly = storeOnly,
      Reason = DispatchReason.RouteDecides,
    };
  }

  private static DispatchDecision _localCallNeverSendsOut(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) {
    if (!policy.Expects(DispatchExpectations.LocalOnly)) {
      return current;
    }
    return current with { WriteOutbox = false, WriteEventStoreOnly = false, Reason = DispatchReason.LocalCallNeverSendsOut };
  }

  private static DispatchDecision _ownedCommandStaysLocal(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) {
    if (!situation.IsCommand || !situation.IsOwned || situation.IsSystemCommand || current.WriteEventStoreOnly) {
      return current;
    }
    // Whatever the route asked for, the command is handled here and nowhere else.
    return current with {
      Local = situation.HasLocalHandler ? LocalRun.Now : LocalRun.None,
      WriteOutbox = false,
      Reason = DispatchReason.OwnedCommandStaysLocal,
    };
  }

  private static DispatchDecision _systemCommandBroadcasts(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) {
    if (!situation.IsSystemCommand || policy.Expects(DispatchExpectations.LocalOnly)) {
      return current;
    }
    return current with {
      Local = situation.HasLocalHandler ? LocalRun.Now : LocalRun.None,
      WriteOutbox = true,
      WriteEventStoreOnly = false,
      Reason = DispatchReason.SystemCommandBroadcasts,
    };
  }

  private static DispatchDecision _commandCannotBeStoreOnly(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
    situation.IsCommand && current.WriteEventStoreOnly
      ? DispatchDecision.Rejected(DispatchRejection.CommandCannotBeStoreOnly, DispatchReason.CommandCannotBeStoreOnly)
      : current;

  private static DispatchDecision _handlerRequired(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) {
    if (situation.HasLocalHandler) {
      return current;
    }
    if (situation.Has(DispatchBehaviors.ThrowWhenNoHandler)) {
      return DispatchDecision.Rejected(DispatchRejection.NoHandler, DispatchReason.HandlerRequired);
    }
    var ownedCommand = situation.IsCommand && situation.IsOwned && !situation.IsSystemCommand;
    var keptHereWithNobodyToHandleIt = situation.IsCommand
      && current.Local == LocalRun.None && !current.WriteOutbox && !current.WriteEventStoreOnly;
    var required = policy.Expects(DispatchExpectations.ThrowWhenNoHandler) || ownedCommand || keptHereWithNobodyToHandleIt;
    if (!required) {
      return current;
    }
    var mayContinue = situation.Has(DispatchBehaviors.ContinueWhenNoHandler) && !situation.ResultRequired;
    return mayContinue
      ? current with { Reason = DispatchReason.HandlerRequired }
      : DispatchDecision.Rejected(DispatchRejection.NoHandler, DispatchReason.HandlerRequired);
  }

  private static DispatchDecision _scheduledDefersBoth(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) {
    if (!situation.IsScheduledInFuture) {
      return current;
    }
    if (!current.WriteOutbox) {
      return DispatchDecision.Rejected(DispatchRejection.CannotScheduleInProcess, DispatchReason.Scheduled);
    }
    if (!situation.OutboxAvailable && !situation.Has(DispatchBehaviors.AllowScheduleWithoutOutbox)) {
      return DispatchDecision.Rejected(DispatchRejection.NoOutbox, DispatchReason.Scheduled);
    }
    return current with {
      Local = current.Local == LocalRun.None ? LocalRun.None : LocalRun.AtScheduledTime,
      DeferUntil = situation.ScheduledFor,
      Reason = DispatchReason.Scheduled,
    };
  }

  private static DispatchDecision _resultRequiresLocalRun(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
    situation.ResultRequired && current.Local != LocalRun.Now
      ? DispatchDecision.Rejected(DispatchRejection.ResultUnavailable, DispatchReason.HandlerRequired)
      : current;

  private static DispatchDecision _noOutbox(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) {
    if (!current.WriteOutbox || situation.OutboxAvailable) {
      return current;
    }
    if (policy.NoOutbox == NoOutboxBehavior.DeferOrLog) {
      // The write goes ahead: the dispatcher hands it to the deferred outbox channel, or logs it
      return current;
    }
    if (current.Local == LocalRun.Now) {
      // Handled here during the call: with nowhere to send it, it simply is not sent on
      return current with { WriteOutbox = false, Reason = DispatchReason.NoOutbox };
    }
    return DispatchDecision.Rejected(DispatchRejection.NoOutbox, DispatchReason.NoOutbox);
  }
}
