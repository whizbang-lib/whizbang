// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Dispatch.Policy;

namespace Whizbang.Core.Tests.Dispatch.Policy;

/// <summary>
/// The dispatch decision allocates nothing (#1395): it runs on every dispatch, including the local
/// fast paths. Measured on the current thread after a warm-up call, so the only bytes counted are the
/// decision's own.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchPolicy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchPolicyEngine.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#performance</docs>
[NotInParallel("AllocationMeasurement")]
public class DispatchPolicyAllocationTests {
  private const int ITERATIONS = 1_000;

  [Test]
  public async Task BuiltInPolicyDecision_AllocatesNothingAsync() {
    var situation = DispatchScenario.ToSituation(ScenarioKind.Command, owned: true, handler: true,
      route: DispatchModes.Outbox, scheduled: false, outbox: true);
    var policy = DispatchPolicies.Send;
    _ = policy.Decide(in situation);

    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < ITERATIONS; i++) {
      _ = policy.Decide(in situation);
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    await Assert.That(allocated).IsEqualTo(0L)
      .Because("the decision is structs and static rules; any allocation here is paid on every dispatch");
  }

  [Test]
  public async Task EngineDecisionWithNoHooks_AllocatesNothingAsync() {
    var situation = DispatchScenario.ToSituation(ScenarioKind.Event, owned: false, handler: true,
      route: null, scheduled: true, outbox: true);
    var engine = DispatchPolicyEngine.Default;
    var policy = engine.Resolve(DispatchPolicies.Publish, requested: null);
    _ = engine.Decide(policy, in situation);

    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < ITERATIONS; i++) {
      _ = engine.Decide(policy, in situation);
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    await Assert.That(allocated).IsEqualTo(0L)
      .Because("with no developer hooks the engine returns the policy's decision directly");
  }

  [Test]
  public async Task RefusedDecision_AllocatesNothingAsync() {
    var situation = DispatchScenario.ToSituation(ScenarioKind.Command, owned: true, handler: false,
      route: null, scheduled: false, outbox: true);
    var policy = DispatchPolicies.Send;
    _ = policy.Decide(in situation);

    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < ITERATIONS; i++) {
      _ = policy.Decide(in situation);
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    await Assert.That(allocated).IsEqualTo(0L);
  }
}
