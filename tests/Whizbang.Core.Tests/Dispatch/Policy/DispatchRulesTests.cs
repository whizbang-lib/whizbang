// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Dispatch.Policy;

namespace Whizbang.Core.Tests.Dispatch.Policy;

/// <summary>
/// Each shared dispatch rule in isolation (#1395): every branch, applied to a baseline decision, so a
/// rule's behavior is pinned independently of the policies that compose it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchRules.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#rules</docs>
public class DispatchRulesTests {
  private static readonly DateTimeOffset _at = DispatchScenario.ScheduledAt;

  private static readonly DispatchDecision _baselineHandled =
    new(LocalRun.Now, WriteOutbox: true, WriteEventStoreOnly: false, DeferUntil: null, DispatchRejection.None, DispatchReason.Default);

  private static readonly DispatchDecision _baselineUnhandled = _baselineHandled with { Local = LocalRun.None };

  private static DispatchSituation _situation(
      MessageTraits traits = MessageTraits.Command,
      bool handler = true,
      DispatchModes? route = null,
      bool scheduled = false,
      bool outbox = true,
      DispatchBehaviors behaviors = DispatchBehaviors.None,
      bool resultRequired = false) =>
    new(typeof(DispatchRulesTests), traits, handler ? LocalHandlers.Generated : LocalHandlers.None, route,
      scheduled ? _at : null, scheduled, outbox, behaviors, resultRequired);

  private static DispatchDecision _apply(DispatchRule rule, DispatchSituation situation, DispatchDecision current, DispatchPolicy? policy = null) =>
    rule(in situation, policy ?? DispatchPolicies.Send, in current);

  // BehaviorsAreConsistent

  [Test]
  public async Task BehaviorsAreConsistent_BothHandlerOverrides_RejectsAsync() {
    var result = _apply(DispatchRules.BehaviorsAreConsistent,
      _situation(behaviors: DispatchBehaviors.ThrowWhenNoHandler | DispatchBehaviors.ContinueWhenNoHandler), _baselineHandled);

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.ConflictingBehaviors);
  }

  [Test]
  [Arguments(DispatchBehaviors.None)]
  [Arguments(DispatchBehaviors.ThrowWhenNoHandler)]
  [Arguments(DispatchBehaviors.ContinueWhenNoHandler)]
  [Arguments(DispatchBehaviors.AllowScheduleWithoutOutbox)]
  public async Task BehaviorsAreConsistent_ConsistentOverrides_LeavesTheDecisionAsync(DispatchBehaviors behaviors) {
    var result = _apply(DispatchRules.BehaviorsAreConsistent, _situation(behaviors: behaviors), _baselineHandled);

    await Assert.That(result).IsEqualTo(_baselineHandled);
  }

  // RouteDecides

  [Test]
  public async Task RouteDecides_NoRouteChosen_LeavesTheDecisionAsync() {
    var result = _apply(DispatchRules.RouteDecides, _situation(route: null), _baselineHandled);

    await Assert.That(result).IsEqualTo(_baselineHandled);
  }

  [Test]
  public async Task RouteDecides_RouteNone_RejectsAsync() {
    var result = _apply(DispatchRules.RouteDecides, _situation(route: DispatchModes.None), _baselineHandled);

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.NoRoute);
  }

  [Test]
  [Arguments(DispatchModes.Both, LocalRun.Now, true, false)]
  [Arguments(DispatchModes.Outbox, LocalRun.None, true, false)]
  [Arguments(DispatchModes.LocalNoPersist, LocalRun.Now, false, false)]
  [Arguments(DispatchModes.Local, LocalRun.Now, false, true)]
  [Arguments(DispatchModes.EventStoreOnly, LocalRun.None, false, true)]
  public async Task RouteDecides_EventRoutes_SetLocalOutboxAndStoreAsync(DispatchModes route, LocalRun local, bool outbox, bool storeOnly) {
    var result = _apply(DispatchRules.RouteDecides, _situation(MessageTraits.Event, route: route), _baselineHandled);

    await Assert.That(result.Local).IsEqualTo(local);
    await Assert.That(result.WriteOutbox).IsEqualTo(outbox);
    await Assert.That(result.WriteEventStoreOnly).IsEqualTo(storeOnly);
    await Assert.That(result.Reason).IsEqualTo(DispatchReason.RouteDecides);
  }

  [Test]
  public async Task RouteDecides_CommandRoutedLocal_IsNotStoredAsync() {
    var result = _apply(DispatchRules.RouteDecides, _situation(MessageTraits.Command, route: DispatchModes.Local), _baselineHandled);

    await Assert.That(result.WriteEventStoreOnly).IsFalse()
      .Because("a command is never stored; Local keeps it here without storing it");
    await Assert.That(result.Local).IsEqualTo(LocalRun.Now);
  }

  [Test]
  public async Task RouteDecides_CommandRoutedEventStoreOnly_IsMarkedStoreOnlyForTheLaterRefusalAsync() {
    var result = _apply(DispatchRules.RouteDecides, _situation(MessageTraits.Command, route: DispatchModes.EventStoreOnly), _baselineHandled);

    await Assert.That(result.WriteEventStoreOnly).IsTrue();
  }

  [Test]
  public async Task RouteDecides_OutboxAlreadyOff_StaysOffAsync() {
    var result = _apply(DispatchRules.RouteDecides, _situation(MessageTraits.Event, route: DispatchModes.Both), _baselineHandled with { WriteOutbox = false });

    await Assert.That(result.WriteOutbox).IsFalse();
  }

  // LocalCallNeverSendsOut

  [Test]
  public async Task LocalCallNeverSendsOut_LocalPolicy_TurnsOffOutboxAndStoreAsync() {
    var result = _apply(DispatchRules.LocalCallNeverSendsOut, _situation(), _baselineHandled with { WriteEventStoreOnly = true }, DispatchPolicies.LocalInvoke);

    await Assert.That(result.WriteOutbox).IsFalse();
    await Assert.That(result.WriteEventStoreOnly).IsFalse();
    await Assert.That(result.Reason).IsEqualTo(DispatchReason.LocalCallNeverSendsOut);
  }

  [Test]
  public async Task LocalCallNeverSendsOut_NonLocalPolicy_LeavesTheDecisionAsync() {
    var result = _apply(DispatchRules.LocalCallNeverSendsOut, _situation(), _baselineHandled, DispatchPolicies.Send);

    await Assert.That(result).IsEqualTo(_baselineHandled);
  }

  // OwnedCommandStaysLocal

  [Test]
  public async Task OwnedCommandStaysLocal_OwnedCommand_StaysHereAsync() {
    var result = _apply(DispatchRules.OwnedCommandStaysLocal, _situation(MessageTraits.Command | MessageTraits.Owned), _baselineHandled);

    await Assert.That(result.WriteOutbox).IsFalse();
    await Assert.That(result.Local).IsEqualTo(LocalRun.Now);
    await Assert.That(result.Reason).IsEqualTo(DispatchReason.OwnedCommandStaysLocal);
  }

  [Test]
  public async Task OwnedCommandStaysLocal_RoutedAwayFromLocal_IsHandledHereAnywayAsync() {
    var result = _apply(DispatchRules.OwnedCommandStaysLocal, _situation(MessageTraits.Command | MessageTraits.Owned), _baselineUnhandled);

    await Assert.That(result.Local).IsEqualTo(LocalRun.Now)
      .Because("Route.Outbox cannot send an owned command away from the service that handles it");
  }

  [Test]
  public async Task OwnedCommandStaysLocal_NoHandler_RunsNothingAsync() {
    var result = _apply(DispatchRules.OwnedCommandStaysLocal, _situation(MessageTraits.Command | MessageTraits.Owned, handler: false), _baselineUnhandled);

    await Assert.That(result.Local).IsEqualTo(LocalRun.None);
    await Assert.That(result.WriteOutbox).IsFalse();
  }

  [Test]
  [Arguments(MessageTraits.Command)]
  [Arguments(MessageTraits.Event | MessageTraits.Owned)]
  [Arguments(MessageTraits.Command | MessageTraits.SystemCommand | MessageTraits.Owned)]
  public async Task OwnedCommandStaysLocal_NotAnOwnedPlainCommand_LeavesTheDecisionAsync(MessageTraits traits) {
    var result = _apply(DispatchRules.OwnedCommandStaysLocal, _situation(traits), _baselineHandled);

    await Assert.That(result).IsEqualTo(_baselineHandled);
  }

  [Test]
  public async Task OwnedCommandStaysLocal_StoreOnly_LeavesItForTheRefusalAsync() {
    var storeOnly = _baselineUnhandled with { WriteOutbox = false, WriteEventStoreOnly = true };

    var result = _apply(DispatchRules.OwnedCommandStaysLocal, _situation(MessageTraits.Command | MessageTraits.Owned), storeOnly);

    await Assert.That(result).IsEqualTo(storeOnly);
  }

  // SystemCommandBroadcasts

  [Test]
  public async Task SystemCommandBroadcasts_RoutedLocal_StillGoesOutAsync() {
    var keptIn = _baselineHandled with { WriteOutbox = false, WriteEventStoreOnly = true };

    var result = _apply(DispatchRules.SystemCommandBroadcasts, _situation(MessageTraits.Command | MessageTraits.SystemCommand), keptIn);

    await Assert.That(result.WriteOutbox).IsTrue();
    await Assert.That(result.WriteEventStoreOnly).IsFalse();
    await Assert.That(result.Local).IsEqualTo(LocalRun.Now);
    await Assert.That(result.Reason).IsEqualTo(DispatchReason.SystemCommandBroadcasts);
  }

  [Test]
  public async Task SystemCommandBroadcasts_NoHandler_GoesOutWithoutRunningAsync() {
    var result = _apply(DispatchRules.SystemCommandBroadcasts, _situation(MessageTraits.Command | MessageTraits.SystemCommand, handler: false), _baselineUnhandled);

    await Assert.That(result.Local).IsEqualTo(LocalRun.None);
    await Assert.That(result.WriteOutbox).IsTrue();
  }

  [Test]
  public async Task SystemCommandBroadcasts_LocalPolicy_LeavesTheDecisionAsync() {
    var keptIn = _baselineHandled with { WriteOutbox = false };

    var result = _apply(DispatchRules.SystemCommandBroadcasts, _situation(MessageTraits.Command | MessageTraits.SystemCommand), keptIn, DispatchPolicies.LocalInvoke);

    await Assert.That(result).IsEqualTo(keptIn);
  }

  [Test]
  public async Task SystemCommandBroadcasts_NotASystemCommand_LeavesTheDecisionAsync() {
    var result = _apply(DispatchRules.SystemCommandBroadcasts, _situation(MessageTraits.Command), _baselineHandled);

    await Assert.That(result).IsEqualTo(_baselineHandled);
  }

  // CommandCannotBeStoreOnly

  [Test]
  public async Task CommandCannotBeStoreOnly_CommandStoreOnly_RejectsAsync() {
    var result = _apply(DispatchRules.CommandCannotBeStoreOnly, _situation(MessageTraits.Command), _baselineUnhandled with { WriteEventStoreOnly = true });

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.CommandCannotBeStoreOnly);
  }

  [Test]
  public async Task CommandCannotBeStoreOnly_EventStoreOnly_LeavesTheDecisionAsync() {
    var storeOnly = _baselineUnhandled with { WriteEventStoreOnly = true };

    var result = _apply(DispatchRules.CommandCannotBeStoreOnly, _situation(MessageTraits.Event), storeOnly);

    await Assert.That(result).IsEqualTo(storeOnly);
  }

  [Test]
  public async Task CommandCannotBeStoreOnly_CommandNotStored_LeavesTheDecisionAsync() {
    var result = _apply(DispatchRules.CommandCannotBeStoreOnly, _situation(MessageTraits.Command), _baselineHandled);

    await Assert.That(result).IsEqualTo(_baselineHandled);
  }

  // HandlerRequired

  [Test]
  public async Task HandlerRequired_HasHandler_LeavesTheDecisionEvenWithTheThrowOverrideAsync() {
    var result = _apply(DispatchRules.HandlerRequired, _situation(behaviors: DispatchBehaviors.ThrowWhenNoHandler), _baselineHandled);

    await Assert.That(result).IsEqualTo(_baselineHandled);
  }

  [Test]
  public async Task HandlerRequired_ThrowOverride_RejectsAnyMissingHandlerAsync() {
    var result = _apply(DispatchRules.HandlerRequired, _situation(MessageTraits.Event, handler: false, behaviors: DispatchBehaviors.ThrowWhenNoHandler), _baselineUnhandled);

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.NoHandler);
  }

  [Test]
  public async Task HandlerRequired_PolicyExpectsHandler_RejectsAsync() {
    var result = _apply(DispatchRules.HandlerRequired, _situation(handler: false), _baselineUnhandled with { WriteOutbox = false }, DispatchPolicies.LocalInvoke);

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.NoHandler);
  }

  [Test]
  public async Task HandlerRequired_OwnedCommand_RejectsAsync() {
    var result = _apply(DispatchRules.HandlerRequired, _situation(MessageTraits.Command | MessageTraits.Owned, handler: false), _baselineUnhandled with { WriteOutbox = false });

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.NoHandler);
  }

  [Test]
  public async Task HandlerRequired_CommandKeptHereWithNobodyToHandleIt_RejectsAsync() {
    var result = _apply(DispatchRules.HandlerRequired, _situation(MessageTraits.Command, handler: false), _baselineUnhandled with { WriteOutbox = false });

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.NoHandler);
  }

  [Test]
  public async Task HandlerRequired_CommandGoingOut_LeavesTheDecisionAsync() {
    var result = _apply(DispatchRules.HandlerRequired, _situation(MessageTraits.Command, handler: false), _baselineUnhandled);

    await Assert.That(result).IsEqualTo(_baselineUnhandled);
  }

  [Test]
  public async Task HandlerRequired_CommandStoredOnly_LeavesItForTheRefusalAsync() {
    var storeOnly = _baselineUnhandled with { WriteOutbox = false, WriteEventStoreOnly = true };

    var result = _apply(DispatchRules.HandlerRequired, _situation(MessageTraits.Command, handler: false), storeOnly);

    await Assert.That(result).IsEqualTo(storeOnly);
  }

  [Test]
  public async Task HandlerRequired_EventKeptHereWithNoSubscriber_IsANoOpAsync() {
    var keptIn = _baselineUnhandled with { WriteOutbox = false };

    var result = _apply(DispatchRules.HandlerRequired, _situation(MessageTraits.Event, handler: false), keptIn);

    await Assert.That(result).IsEqualTo(keptIn);
  }

  [Test]
  public async Task HandlerRequired_ContinueOverride_GoesAheadAsync() {
    var keptIn = _baselineUnhandled with { WriteOutbox = false };

    var result = _apply(DispatchRules.HandlerRequired, _situation(MessageTraits.Command | MessageTraits.Owned, handler: false, behaviors: DispatchBehaviors.ContinueWhenNoHandler), keptIn);

    await Assert.That(result.IsRejected).IsFalse();
    await Assert.That(result.Reason).IsEqualTo(DispatchReason.HandlerRequired);
  }

  [Test]
  public async Task HandlerRequired_ContinueOverrideButResultRequired_RejectsAsync() {
    var result = _apply(DispatchRules.HandlerRequired,
      _situation(handler: false, behaviors: DispatchBehaviors.ContinueWhenNoHandler, resultRequired: true),
      _baselineUnhandled with { WriteOutbox = false }, DispatchPolicies.LocalInvoke);

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.NoHandler)
      .Because("a call that returns the handler's result has nothing to return without one");
  }

  // ScheduledDefersBoth

  [Test]
  public async Task ScheduledDefersBoth_NotScheduled_LeavesTheDecisionAsync() {
    var result = _apply(DispatchRules.ScheduledDefersBoth, _situation(), _baselineHandled);

    await Assert.That(result).IsEqualTo(_baselineHandled);
  }

  [Test]
  public async Task ScheduledDefersBoth_NeverLeavesTheService_RejectsAsync() {
    var result = _apply(DispatchRules.ScheduledDefersBoth, _situation(scheduled: true), _baselineHandled with { WriteOutbox = false });

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.CannotScheduleInProcess);
  }

  [Test]
  public async Task ScheduledDefersBoth_NoOutbox_RejectsAsync() {
    var result = _apply(DispatchRules.ScheduledDefersBoth, _situation(scheduled: true, outbox: false), _baselineHandled);

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.NoOutbox);
  }

  [Test]
  public async Task ScheduledDefersBoth_NoOutboxWithAllowOverride_DefersAsync() {
    var result = _apply(DispatchRules.ScheduledDefersBoth,
      _situation(scheduled: true, outbox: false, behaviors: DispatchBehaviors.AllowScheduleWithoutOutbox), _baselineHandled);

    await Assert.That(result.Local).IsEqualTo(LocalRun.AtScheduledTime);
    await Assert.That(result.DeferUntil).IsEqualTo(_at);
  }

  [Test]
  public async Task ScheduledDefersBoth_Handled_RunsWhenItComesBackAsync() {
    var result = _apply(DispatchRules.ScheduledDefersBoth, _situation(scheduled: true), _baselineHandled);

    await Assert.That(result.Local).IsEqualTo(LocalRun.AtScheduledTime);
    await Assert.That(result.DeferUntil).IsEqualTo(_at);
    await Assert.That(result.MarkHandledLocally).IsFalse()
      .Because("the scheduled copy must be handled by this service when it comes back, so it is not marked");
    await Assert.That(result.Reason).IsEqualTo(DispatchReason.Scheduled);
  }

  [Test]
  public async Task ScheduledDefersBoth_NotHandledHere_OnlyTheCopyIsDeferredAsync() {
    var result = _apply(DispatchRules.ScheduledDefersBoth, _situation(handler: false, scheduled: true), _baselineUnhandled);

    await Assert.That(result.Local).IsEqualTo(LocalRun.None);
    await Assert.That(result.DeferUntil).IsEqualTo(_at);
  }

  // NoOutbox

  [Test]
  public async Task NoOutbox_OutboxAvailable_LeavesTheDecisionAsync() {
    var result = _apply(DispatchRules.NoOutbox, _situation(outbox: true), _baselineUnhandled);

    await Assert.That(result).IsEqualTo(_baselineUnhandled);
  }

  [Test]
  public async Task NoOutbox_NotWritingTheOutbox_LeavesTheDecisionAsync() {
    var keptIn = _baselineHandled with { WriteOutbox = false };

    var result = _apply(DispatchRules.NoOutbox, _situation(outbox: false), keptIn);

    await Assert.That(result).IsEqualTo(keptIn);
  }

  [Test]
  public async Task NoOutbox_HandledNow_IsSimplyNotSentOnAsync() {
    var result = _apply(DispatchRules.NoOutbox, _situation(outbox: false), _baselineHandled);

    await Assert.That(result.WriteOutbox).IsFalse();
    await Assert.That(result.Local).IsEqualTo(LocalRun.Now);
    await Assert.That(result.Reason).IsEqualTo(DispatchReason.NoOutbox);
  }

  [Test]
  public async Task NoOutbox_NotHandledAndPolicyThrows_RejectsAsync() {
    var result = _apply(DispatchRules.NoOutbox, _situation(handler: false, outbox: false), _baselineUnhandled, DispatchPolicies.Send);

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.NoOutbox);
  }

  [Test]
  public async Task NoOutbox_NotHandledAndPolicyDefers_KeepsTheOutboxWriteForTheFallbackAsync() {
    var result = _apply(DispatchRules.NoOutbox, _situation(MessageTraits.Event, handler: false, outbox: false), _baselineUnhandled, DispatchPolicies.Publish);

    await Assert.That(result).IsEqualTo(_baselineUnhandled);
  }

  // ResultRequiresLocalRun

  [Test]
  [Arguments(LocalRun.None)]
  [Arguments(LocalRun.AtScheduledTime)]
  public async Task ResultRequiresLocalRun_HandlerNotRunDuringTheCall_RejectsAsync(LocalRun local) {
    var result = _apply(DispatchRules.ResultRequiresLocalRun, _situation(resultRequired: true), _baselineHandled with { Local = local });

    await Assert.That(result.Reject).IsEqualTo(DispatchRejection.ResultUnavailable);
  }

  [Test]
  public async Task ResultRequiresLocalRun_HandlerRunsNow_LeavesTheDecisionAsync() {
    var result = _apply(DispatchRules.ResultRequiresLocalRun, _situation(resultRequired: true), _baselineHandled);

    await Assert.That(result).IsEqualTo(_baselineHandled);
  }

  [Test]
  public async Task ResultRequiresLocalRun_ResultNotRequired_LeavesTheDecisionAsync() {
    var result = _apply(DispatchRules.ResultRequiresLocalRun, _situation(), _baselineUnhandled);

    await Assert.That(result).IsEqualTo(_baselineUnhandled);
  }
}
