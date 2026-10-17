// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Dispatch.Policy;

namespace Whizbang.Core.Tests.Dispatch.Policy;

/// <summary>
/// The dispatch policy extension points (#1395): developer hooks that adjust a decision, the guard that
/// refuses impossible decisions, the registry of custom policies, the engine that puts them together,
/// and the per-call options that select a policy and override its defaults.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchPolicyHookChain.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchDecisionGuard.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchPolicyRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchPolicyEngine.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#extending</docs>
public class DispatchPolicyExtensionTests {
  private static readonly DispatchDecision _sendOut =
    new(LocalRun.None, WriteOutbox: true, WriteEventStoreOnly: false, DeferUntil: null, DispatchRejection.None, DispatchReason.Default);

  private static DispatchSituation _situation(bool handler = true, MessageTraits traits = MessageTraits.Command, bool resultRequired = false) =>
    new(typeof(DispatchPolicyExtensionTests), traits, handler ? LocalHandlers.Generated : LocalHandlers.None,
      Route: null, ScheduledFor: null, IsScheduledInFuture: false, OutboxAvailable: true, ResultRequired: resultRequired);

  private sealed class RecordingHook(int order, List<int> calls, Func<DispatchDecision, DispatchDecision>? change = null) : IDispatchPolicyHook {
    public int Order => order;

    public DispatchDecision Decide(DispatchPolicy policy, in DispatchSituation situation, in DispatchDecision proposed) {
      calls.Add(order);
      return change is null ? proposed : change(proposed);
    }
  }

  // Hook chain

  [Test]
  public async Task HookChain_NoHooks_IsEmptyAndReturnsTheProposalAsync() {
    var chain = new DispatchPolicyHookChain([]);
    var situation = _situation();

    await Assert.That(chain.IsEmpty).IsTrue();
    await Assert.That(chain.Apply(DispatchPolicies.Send, in situation, _sendOut)).IsEqualTo(_sendOut);
  }

  [Test]
  public async Task HookChain_RunsHooksInOrder_EachSeeingThePreviousResultAsync() {
    var calls = new List<int>();
    var chain = new DispatchPolicyHookChain([
      new RecordingHook(20, calls, d => d with { WriteOutbox = !d.WriteOutbox }),
      new RecordingHook(10, calls, d => d with { WriteOutbox = !d.WriteOutbox }),
      new RecordingHook(30, calls),
    ]);
    var situation = _situation();

    var result = chain.Apply(DispatchPolicies.Send, in situation, _sendOut);

    await Assert.That(chain.IsEmpty).IsFalse();
    await Assert.That(calls).IsEquivalentTo([10, 20, 30]);
    await Assert.That(result.WriteOutbox).IsTrue()
      .Because("two toggles cancel out only if the second hook saw the first one's result");
  }

  [Test]
  public async Task HookChain_AChange_IsRecordedAsAHookDecisionAsync() {
    var chain = new DispatchPolicyHookChain([new RecordingHook(1, [], d => d with { WriteOutbox = false })]);
    var situation = _situation();

    var result = chain.Apply(DispatchPolicies.Send, in situation, _sendOut);

    await Assert.That(result.WriteOutbox).IsFalse();
    await Assert.That(result.Reason).IsEqualTo(DispatchReason.Hook);
  }

  [Test]
  public async Task HookChain_NoOpinion_KeepsTheReasonAsync() {
    var chain = new DispatchPolicyHookChain([new RecordingHook(1, [])]);
    var situation = _situation();

    var result = chain.Apply(DispatchPolicies.Send, in situation, _sendOut);

    await Assert.That(result).IsEqualTo(_sendOut);
  }

  [Test]
  public async Task HookChain_NullHooks_ThrowsAsync() {
    await Assert.That(() => new DispatchPolicyHookChain(null!)).Throws<ArgumentNullException>();
  }

  // Guard

  public sealed record ImpossibleDecision(string Name, DispatchPolicy Policy, DispatchSituation Situation, DispatchDecision Decision) {
    public override string ToString() => Name;
  }

  public static IEnumerable<Func<ImpossibleDecision>> ImpossibleDecisions() {
    var send = DispatchPolicies.Send;
    var local = DispatchPolicies.LocalInvoke;
    var now = new DispatchDecision(LocalRun.Now, false, false, null, DispatchRejection.None, DispatchReason.Hook);
    yield return () => new ImpossibleDecision("LocalRunWithNoHandler", send, _situation(handler: false), now);
    yield return () => new ImpossibleDecision("LocalCallWritesOutbox", local, _situation(), now with { WriteOutbox = true });
    yield return () => new ImpossibleDecision("LocalCallStoresEvent", local, _situation(traits: MessageTraits.Event), now with { WriteEventStoreOnly = true });
    yield return () => new ImpossibleDecision("ResultRequiredWithoutLocalRun", send, _situation(resultRequired: true), now with { Local = LocalRun.None, WriteOutbox = true });
    yield return () => new ImpossibleDecision("CommandStoredOnly", send, _situation(), now with { Local = LocalRun.None, WriteEventStoreOnly = true });
    yield return () => new ImpossibleDecision("DeferredLocalRunWithoutOutboxCopy", send, _situation(), now with { Local = LocalRun.AtScheduledTime });
    yield return () => new ImpossibleDecision("DeliveryTimeWithoutOutboxCopy", send, _situation(), now with { DeferUntil = DispatchScenario.ScheduledAt });
  }

  [Test]
  [MethodDataSource(nameof(ImpossibleDecisions))]
  public async Task Guard_ImpossibleDecision_ThrowsAsync(ImpossibleDecision testCase) {
    var situation = testCase.Situation;
    var decision = testCase.Decision;

    await Assert.That(() => DispatchDecisionGuard.Validate(testCase.Policy, in situation, in decision))
      .Throws<InvalidDispatchPolicyException>()
      .Because(testCase.Name);
  }

  [Test]
  public async Task Guard_EveryBuiltInDecision_IsValidAsync() {
    var refused = new List<string>();
    foreach (var (policy, kind, owned, handler, route, scheduled, outbox, behaviors, resultRequired) in DispatchScenarios.AllCombinations()) {
      var situation = DispatchScenario.ToSituation(kind, owned, handler, route, scheduled, outbox, behaviors, resultRequired);
      var decision = policy.Decide(in situation);
      try {
        DispatchDecisionGuard.Validate(policy, in situation, in decision);
      } catch (InvalidDispatchPolicyException ex) {
        refused.Add(ex.Message);
      }
    }

    await Assert.That(refused).IsEmpty().Because("no built-in decision is refused by the guard");
  }

  [Test]
  public async Task Guard_OverridingOwnershipOrBroadcast_IsAllowedAsync() {
    var ownedCommand = _situation(traits: MessageTraits.Command | MessageTraits.Owned);
    var sentOutAnyway = new DispatchDecision(LocalRun.Now, true, false, null, DispatchRejection.None, DispatchReason.Hook);

    await Assert.That(() => DispatchDecisionGuard.Validate(DispatchPolicies.Send, in ownedCommand, in sentOutAnyway))
      .ThrowsNothing()
      .Because("a hook may send an owned command out; that is what hooks are for");
  }

  [Test]
  public async Task Guard_Exception_NamesThePolicyAndTheProblemAsync() {
    var situation = _situation(handler: false);
    var now = new DispatchDecision(LocalRun.Now, false, false, null, DispatchRejection.None, DispatchReason.Hook);

    var exception = await Assert.That(() => DispatchDecisionGuard.Validate(DispatchPolicies.Send, in situation, in now))
      .Throws<InvalidDispatchPolicyException>();

    await Assert.That(exception!.Message).Contains("Send");
    await Assert.That(exception.Message).Contains("no local handler");
  }

  [Test]
  public async Task GuardException_StandardConstructors_KeepMessageAndCauseAsync() {
    var cause = new InvalidOperationException("cause");

    await Assert.That(new InvalidDispatchPolicyException().Message).IsNotEmpty();
    await Assert.That(new InvalidDispatchPolicyException("why", cause).InnerException).IsSameReferenceAs(cause);
    await Assert.That(new InvalidDispatchPolicyException("why", cause).Message).IsEqualTo("why");
  }

  [Test]
  public async Task RejectedException_Constructors_KeepTheirPartsAsync() {
    var cause = new InvalidOperationException("cause");
    var full = new DispatchRejectedException(DispatchRejection.CannotScheduleInProcess, typeof(string), "why");

    await Assert.That(new DispatchRejectedException().Message).IsNotEmpty();
    await Assert.That(new DispatchRejectedException("why").Message).IsEqualTo("why");
    await Assert.That(new DispatchRejectedException("why", cause).InnerException).IsSameReferenceAs(cause);
    await Assert.That(full.Rejection).IsEqualTo(DispatchRejection.CannotScheduleInProcess);
    await Assert.That(full.MessageType).IsEqualTo(typeof(string));
    await Assert.That(new DispatchRejectedException("why").MessageType).IsNull();
  }

  [Test]
  public async Task Options_BatchFailure_DefaultsToStopAndChainsAsync() {
    var options = new DispatchOptions();

    await Assert.That(options.BatchFailure).IsEqualTo(BatchFailureMode.Stop);
    await Assert.That(options.WithBatchFailure(BatchFailureMode.ContinueAndAggregate)).IsSameReferenceAs(options);
    await Assert.That(options.BatchFailure).IsEqualTo(BatchFailureMode.ContinueAndAggregate);
  }

  // Registry

  [Test]
  public async Task Registry_ResolvesEveryBuiltInPolicyByIdAsync() {
    var registry = new DispatchPolicyRegistry();

    foreach (var policy in DispatchPolicies.All) {
      await Assert.That(registry.GetPolicy(policy.Id)).IsSameReferenceAs(policy);
    }
  }

  [Test]
  public async Task Registry_ResolvesARegisteredCustomPolicyAsync() {
    var registry = new DispatchPolicyRegistry();
    var custom = new DispatchPolicy(new DispatchPolicyId(1001), "TenantLocal", DispatchExpectations.None, NoOutboxBehavior.Throw,
      DispatchRules.OwnedCommandStaysLocal);

    registry.Register(custom);

    await Assert.That(registry.GetPolicy(custom.Id)).IsSameReferenceAs(custom);
  }

  [Test]
  public async Task Registry_BuiltInRangeId_ThrowsAsync() {
    var registry = new DispatchPolicyRegistry();
    var tooLow = new DispatchPolicy(new DispatchPolicyId(7), "Clash", DispatchExpectations.None, NoOutboxBehavior.Throw);

    await Assert.That(() => registry.Register(tooLow)).Throws<ArgumentException>();
  }

  [Test]
  public async Task Registry_DuplicateCustomId_ThrowsAsync() {
    var registry = new DispatchPolicyRegistry();
    registry.Register(new DispatchPolicy(new DispatchPolicyId(1002), "First", DispatchExpectations.None, NoOutboxBehavior.Throw));

    await Assert.That(() => registry.Register(new DispatchPolicy(new DispatchPolicyId(1002), "Second", DispatchExpectations.None, NoOutboxBehavior.Throw)))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Registry_NullPolicy_ThrowsAsync() {
    await Assert.That(() => new DispatchPolicyRegistry().Register(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  [Arguments(0)]
  [Arguments(99)]
  [Arguments(5000)]
  public async Task Registry_UnknownId_ThrowsAClearErrorAsync(int id) {
    var exception = await Assert.That(() => new DispatchPolicyRegistry().GetPolicy(new DispatchPolicyId(id)))
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
  }

  // Engine

  [Test]
  public async Task Engine_NoHooks_DecidesExactlyAsThePolicyDoesAsync() {
    var engine = new DispatchPolicyEngine(new DispatchPolicyRegistry(), new DispatchPolicyHookChain([]));

    foreach (var row in DispatchScenarios.All) {
      var situation = row.Situation;
      await Assert.That(engine.Decide(row.Policy, in situation)).IsEqualTo(row.Policy.Decide(in situation));
    }
  }

  [Test]
  public async Task Engine_Hook_ChangesTheDecisionAsync() {
    var engine = new DispatchPolicyEngine(new DispatchPolicyRegistry(),
      new DispatchPolicyHookChain([new RecordingHook(1, [], d => d with { WriteOutbox = false })]));
    var situation = _situation();

    var decision = engine.Decide(DispatchPolicies.Send, in situation);

    await Assert.That(decision.WriteOutbox).IsFalse();
    await Assert.That(decision.Local).IsEqualTo(LocalRun.Now);
  }

  [Test]
  public async Task Engine_HookProducingAnImpossibleDecision_ThrowsAsync() {
    var engine = new DispatchPolicyEngine(new DispatchPolicyRegistry(),
      new DispatchPolicyHookChain([new RecordingHook(1, [], d => d with { WriteOutbox = true })]));
    var situation = _situation();

    await Assert.That(() => engine.Decide(DispatchPolicies.LocalInvoke, in situation)).Throws<InvalidDispatchPolicyException>();
  }

  [Test]
  public async Task Engine_HookCannotUndoARejection_WithoutTheGuardSeeingItAsync() {
    var engine = new DispatchPolicyEngine(new DispatchPolicyRegistry(),
      new DispatchPolicyHookChain([new RecordingHook(1, [], d => d with { Reject = DispatchRejection.None })]));
    var situation = _situation(handler: false);

    var decision = engine.Decide(DispatchPolicies.LocalInvoke, in situation);

    await Assert.That(decision.IsRejected).IsFalse()
      .Because("a hook may lift a refusal; the result (nothing runs, nothing is sent) is still a valid decision");
    await Assert.That(decision.Local).IsEqualTo(LocalRun.None);
  }

  [Test]
  public async Task Engine_Resolve_UsesTheCallsPolicyUnlessTheCallerChoseOneAsync() {
    var registry = new DispatchPolicyRegistry();
    var custom = new DispatchPolicy(new DispatchPolicyId(1003), "Mine", DispatchExpectations.None, NoOutboxBehavior.Throw);
    registry.Register(custom);
    var engine = new DispatchPolicyEngine(registry, new DispatchPolicyHookChain([]));

    await Assert.That(engine.Resolve(DispatchPolicies.Send, requested: null)).IsSameReferenceAs(DispatchPolicies.Send);
    await Assert.That(engine.Resolve(DispatchPolicies.Send, custom.Id)).IsSameReferenceAs(custom);
  }

  [Test]
  public async Task Engine_Default_HasTheBuiltInsAndNoHooksAsync() {
    var engine = DispatchPolicyEngine.Default;
    var situation = _situation();

    await Assert.That(engine.Resolve(DispatchPolicies.Publish, DispatchPolicies.Cascade.Id)).IsSameReferenceAs(DispatchPolicies.Cascade);
    await Assert.That(engine.Decide(DispatchPolicies.Send, in situation)).IsEqualTo(DispatchPolicies.Send.Decide(in situation));
  }

  [Test]
  public async Task Engine_NullArguments_ThrowAsync() {
    await Assert.That(() => new DispatchPolicyEngine(null!, new DispatchPolicyHookChain([]))).Throws<ArgumentNullException>();
    await Assert.That(() => new DispatchPolicyEngine(new DispatchPolicyRegistry(), null!)).Throws<ArgumentNullException>();
  }

  // Options

  [Test]
  public async Task Options_PolicyAndBehaviors_DefaultToNoneAndChainAsync() {
    var options = new DispatchOptions();

    await Assert.That(options.Policy).IsNull();
    await Assert.That(options.Behaviors).IsEqualTo(DispatchBehaviors.None);

    var chained = options.WithPolicy(new DispatchPolicyId(1004)).WithBehaviors(DispatchBehaviors.ContinueWhenNoHandler);

    await Assert.That(chained).IsSameReferenceAs(options);
    await Assert.That(options.Policy).IsEqualTo(new DispatchPolicyId(1004));
    await Assert.That(options.Behaviors).IsEqualTo(DispatchBehaviors.ContinueWhenNoHandler);
  }
}
