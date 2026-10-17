// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Dispatch.Policy;

namespace Whizbang.Core.Tests.Dispatch.Policy;

/// <summary>
/// The policy type itself (#1395): construction, expectations, the order its rules run in, and the
/// situation and decision helpers it relies on.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchPolicy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchPolicies.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchSituation.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchDecision.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#policies</docs>
public class DispatchPolicyTests {

  [Test]
  public async Task Constructor_BlankName_ThrowsAsync() {
    await Assert.That(() => new DispatchPolicy(new DispatchPolicyId(1000), " ", DispatchExpectations.None, NoOutboxBehavior.Throw))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_NullRules_ThrowsAsync() {
    await Assert.That(() => new DispatchPolicy(new DispatchPolicyId(1000), "Custom", DispatchExpectations.None, NoOutboxBehavior.Throw, null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_CopiesTheRules_SoTheCallerCannotChangeThemLaterAsync() {
    DispatchRule[] rules = [DispatchRules.RouteDecides];
    var policy = new DispatchPolicy(new DispatchPolicyId(1000), "Custom", DispatchExpectations.None, NoOutboxBehavior.Throw, rules);

    rules[0] = DispatchRules.NoOutbox;

    await Assert.That(policy.Rules[0]).IsEqualTo(DispatchRules.RouteDecides);
  }

  [Test]
  public async Task Properties_ReturnWhatWasGivenAsync() {
    var policy = new DispatchPolicy(new DispatchPolicyId(1234), "Custom", DispatchExpectations.LocalOnly, NoOutboxBehavior.DeferOrLog);

    await Assert.That(policy.Id).IsEqualTo(new DispatchPolicyId(1234));
    await Assert.That(policy.Name).IsEqualTo("Custom");
    await Assert.That(policy.Expectations).IsEqualTo(DispatchExpectations.LocalOnly);
    await Assert.That(policy.NoOutbox).IsEqualTo(NoOutboxBehavior.DeferOrLog);
    await Assert.That(policy.ToString()).IsEqualTo("Custom");
  }

  [Test]
  public async Task Expects_RequiresEveryRequestedFlagAsync() {
    var policy = DispatchPolicies.LocalInvoke;

    await Assert.That(policy.Expects(DispatchExpectations.LocalOnly)).IsTrue();
    await Assert.That(policy.Expects(DispatchExpectations.LocalOnly | DispatchExpectations.ThrowWhenNoHandler)).IsTrue();
    await Assert.That(policy.Expects(DispatchExpectations.LocalOnly | DispatchExpectations.FanOutAllReceptors)).IsFalse();
  }

  [Test]
  public async Task Decide_NoRules_IsTheBaselineAsync() {
    var policy = new DispatchPolicy(new DispatchPolicyId(1000), "Bare", DispatchExpectations.None, NoOutboxBehavior.Throw);
    var handled = DispatchScenario.ToSituation(ScenarioKind.Command, owned: false, handler: true, route: null, scheduled: false, outbox: true);
    var unhandled = DispatchScenario.ToSituation(ScenarioKind.Command, owned: false, handler: false, route: null, scheduled: false, outbox: true);

    await Assert.That(policy.Decide(in handled).Local).IsEqualTo(LocalRun.Now);
    await Assert.That(policy.Decide(in handled).WriteOutbox).IsTrue();
    await Assert.That(policy.Decide(in unhandled).Local).IsEqualTo(LocalRun.None);
    await Assert.That(policy.Decide(in unhandled).Reason).IsEqualTo(DispatchReason.Default);
  }

  [Test]
  public async Task Decide_StopsAtTheFirstRejectionAsync() {
    var ran = false;
    static DispatchDecision reject(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) =>
      DispatchDecision.Rejected(DispatchRejection.NoRoute, DispatchReason.RouteDecides);
    DispatchDecision after(in DispatchSituation situation, DispatchPolicy policy, in DispatchDecision current) {
      ran = true;
      return current;
    }
    var policy = new DispatchPolicy(new DispatchPolicyId(1000), "Stops", DispatchExpectations.None, NoOutboxBehavior.Throw, reject, after);
    var situation = DispatchScenario.ToSituation(ScenarioKind.Command, owned: false, handler: true, route: null, scheduled: false, outbox: true);

    var decision = policy.Decide(in situation);

    await Assert.That(decision.Reject).IsEqualTo(DispatchRejection.NoRoute);
    await Assert.That(ran).IsFalse();
  }

  [Test]
  public async Task RuleOrder_Matters_OwnedBeforeRouteWouldLetRouteOutboxDropTheHandlerAsync() {
    // The built-in order applies the route first and the owned rule after it, so Route.Outbox cannot
    // take an owned command's handling away. Swapping them changes the outcome, which proves the
    // order is part of the policy.
    var swapped = new DispatchPolicy(new DispatchPolicyId(1000), "Swapped", DispatchExpectations.None, NoOutboxBehavior.Throw,
      DispatchRules.OwnedCommandStaysLocal, DispatchRules.RouteDecides);
    var situation = DispatchScenario.ToSituation(ScenarioKind.Command, owned: true, handler: true, route: DispatchModes.Outbox, scheduled: false, outbox: true);

    var builtIn = DispatchPolicies.Send.Decide(in situation);
    var reordered = swapped.Decide(in situation);

    await Assert.That(builtIn.Local).IsEqualTo(LocalRun.Now);
    await Assert.That(reordered.Local).IsEqualTo(LocalRun.None);
  }

  [Test]
  public async Task BuiltInPolicies_HaveDistinctIdsBelowTheCustomRangeAsync() {
    var ids = DispatchPolicies.All.Select(p => p.Id).ToList();

    await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count);
    await Assert.That(ids.All(id => !id.IsCustom)).IsTrue();
    await Assert.That(new DispatchPolicyId(DispatchPolicyId.FIRST_CUSTOM_VALUE).IsCustom).IsTrue();
  }

  [Test]
  public async Task BuiltInPolicies_EveryPolicyChecksTheOverridesFirstAsync() {
    foreach (var policy in DispatchPolicies.All) {
      await Assert.That(policy.Rules[0]).IsEqualTo(DispatchRules.BehaviorsAreConsistent)
        .Because($"{policy.Name} must refuse contradictory overrides before any other rule reads them");
    }
  }

  [Test]
  public async Task Situation_Helpers_ReadTheTraitsAndHandlersAsync() {
    var situation = new DispatchSituation(typeof(object),
      MessageTraits.Command | MessageTraits.SystemCommand | MessageTraits.Owned,
      LocalHandlers.Runtime, Route: null, ScheduledFor: null, IsScheduledInFuture: false, OutboxAvailable: true,
      DispatchBehaviors.ContinueWhenNoHandler);
    var hookOnly = situation with { Traits = MessageTraits.Event, Handlers = LocalHandlers.ExplicitLocalStageHook };

    await Assert.That(situation.MessageType).IsEqualTo(typeof(object));
    await Assert.That(situation.IsCommand).IsTrue();
    await Assert.That(situation.IsSystemCommand).IsTrue();
    await Assert.That(situation.IsOwned).IsTrue();
    await Assert.That(situation.HasLocalHandler).IsTrue();
    await Assert.That(situation.Has(DispatchBehaviors.ContinueWhenNoHandler)).IsTrue();
    await Assert.That(situation.Has(DispatchBehaviors.ThrowWhenNoHandler)).IsFalse();
    await Assert.That(hookOnly.IsCommand).IsFalse();
    await Assert.That(hookOnly.HasLocalHandler).IsFalse()
      .Because("a stage hook runs alongside a handler; on its own it is not one");
  }

  [Test]
  public async Task Decision_Helpers_ReportTheMarkAndTheRefusalAsync() {
    var now = new DispatchDecision(LocalRun.Now, true, false, null, DispatchRejection.None, DispatchReason.Default);
    var later = now with { Local = LocalRun.AtScheduledTime };
    var refused = DispatchDecision.Rejected(DispatchRejection.NoHandler, DispatchReason.HandlerRequired);

    await Assert.That(now.MarkHandledLocally).IsTrue();
    await Assert.That(later.MarkHandledLocally).IsFalse();
    await Assert.That((now with { WriteOutbox = false }).MarkHandledLocally).IsFalse();
    await Assert.That(now.IsRejected).IsFalse();
    await Assert.That(refused.IsRejected).IsTrue();
    await Assert.That(refused.Local).IsEqualTo(LocalRun.None);
    await Assert.That(refused.WriteOutbox).IsFalse();
    await Assert.That(refused.Reason).IsEqualTo(DispatchReason.HandlerRequired);
  }
}
