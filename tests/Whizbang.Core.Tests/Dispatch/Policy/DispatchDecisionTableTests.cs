// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Dispatch.Policy;

namespace Whizbang.Core.Tests.Dispatch.Policy;

/// <summary>
/// The dispatch decision table (#1395), checked against the built-in policies alone. Each row is a
/// decision the owner fixed; the combination check makes sure no situation is left out of the table.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchPolicies.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatch/Policy/DispatchRules.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
public class DispatchDecisionTableTests {

  public static IEnumerable<Func<DispatchScenario>> Rows() => DispatchScenarios.All.Select(row => (Func<DispatchScenario>)(() => row));

  [Test]
  [MethodDataSource(nameof(Rows))]
  public async Task Row_DecidesAsTheTableSaysAsync(DispatchScenario row) {
    var situation = row.Situation;

    var decision = row.Policy.Decide(in situation);

    await Assert.That(decision with { Reason = default }).IsEqualTo(row.ExpectedDecision)
      .Because($"row {row.Row} ({row}) is fixed by the decision table");
    await Assert.That(decision.MarkHandledLocally).IsEqualTo(row.ExpectedMarked)
      .Because("the outbox copy is marked exactly when this service handled the message during the call and also sent it");
  }

  [Test]
  public async Task Rows_HaveUniqueNumbersAsync() {
    var duplicates = DispatchScenarios.All.GroupBy(r => r.Row).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

    await Assert.That(duplicates).IsEmpty();
  }

  [Test]
  public async Task EveryCombination_BehavesLikeANamedRowAsync() {
    // Group every combination of inputs by the decision its policy makes. Each distinct decision of a
    // policy must be shown by at least one named row, so a behavior nobody named fails here until it
    // is added to the table (and to the docs table built from it).
    var named = DispatchScenarios.All
      .Select(r => (r.Policy, Decision: _withoutReason(r.Policy.Decide(r.Situation))))
      .ToHashSet();

    // One example per distinct unnamed decision, so the failure lists what to add, once each.
    var unnamed = new Dictionary<(DispatchPolicy, DispatchDecision), string>();
    foreach (var (policy, kind, owned, handler, route, scheduled, outbox, behaviors, resultRequired) in DispatchScenarios.AllCombinations()) {
      var situation = DispatchScenario.ToSituation(kind, owned, handler, route, scheduled, outbox, behaviors, resultRequired);
      var decision = _withoutReason(policy.Decide(in situation));
      if (!named.Contains((policy, decision))) {
        unnamed.TryAdd((policy, decision),
          $"{policy.Name}: {kind} owned={owned} handler={handler} route={route?.ToString() ?? "none chosen"} scheduled={scheduled} outbox={outbox} behaviors={behaviors} resultRequired={resultRequired} -> " +
          $"local={decision.Local} outbox={decision.WriteOutbox} storeOnly={decision.WriteEventStoreOnly} deferred={decision.DeferUntil is not null} reject={decision.Reject}");
      }
    }

    await Assert.That(unnamed.Count).IsEqualTo(0)
      .Because("every distinct decision of a policy must have a named row in the decision table. Unnamed:\n" + string.Join("\n", unnamed.Values));
  }

  [Test]
  public async Task EveryCombination_KeepsTheInvariantsAsync() {
    var broken = new List<string>();
    foreach (var (policy, kind, owned, handler, route, scheduled, outbox, behaviors, resultRequired) in DispatchScenarios.AllCombinations()) {
      var situation = DispatchScenario.ToSituation(kind, owned, handler, route, scheduled, outbox, behaviors, resultRequired);
      var decision = policy.Decide(in situation);
      var label = $"{policy.Name}: {kind} owned={owned} handler={handler} route={route} scheduled={scheduled} outbox={outbox} behaviors={behaviors} resultRequired={resultRequired}";

      if (policy.Expects(DispatchExpectations.LocalOnly) && (decision.WriteOutbox || decision.WriteEventStoreOnly)) {
        broken.Add($"{label}: a local call left the service");
      }
      if (kind == ScenarioKind.Command && owned && decision.WriteOutbox) {
        broken.Add($"{label}: an owned command was written to the outbox");
      }
      if (decision.Local != LocalRun.None && !handler) {
        broken.Add($"{label}: local handlers run where there are none");
      }
      if (decision.IsRejected && (decision.Local != LocalRun.None || decision.WriteOutbox || decision.WriteEventStoreOnly)) {
        broken.Add($"{label}: a refused dispatch still does something");
      }
      if (decision.Local == LocalRun.AtScheduledTime && !decision.WriteOutbox) {
        broken.Add($"{label}: deferred local handling has no outbox copy to come back");
      }
      if (!decision.IsRejected && decision.Local == LocalRun.None && !decision.WriteOutbox && !decision.WriteEventStoreOnly
          && kind != ScenarioKind.Event && !behaviors.HasFlag(DispatchBehaviors.ContinueWhenNoHandler)) {
        broken.Add($"{label}: a command silently does nothing");
      }
      if (!decision.IsRejected && !handler && behaviors.HasFlag(DispatchBehaviors.ThrowWhenNoHandler) && !behaviors.HasFlag(DispatchBehaviors.ContinueWhenNoHandler)) {
        broken.Add($"{label}: no handler with the throw override, yet it went ahead");
      }
      if (!decision.IsRejected && behaviors.HasFlag(DispatchBehaviors.ThrowWhenNoHandler | DispatchBehaviors.ContinueWhenNoHandler)) {
        broken.Add($"{label}: contradictory overrides were accepted");
      }
      if (!decision.IsRejected && scheduled && !outbox && !behaviors.HasFlag(DispatchBehaviors.AllowScheduleWithoutOutbox)) {
        broken.Add($"{label}: a schedule was accepted with no outbox to hold it");
      }
      if (decision.WriteOutbox && !outbox && policy.NoOutbox == NoOutboxBehavior.Throw) {
        broken.Add($"{label}: the outbox is written where there is none");
      }
      if (decision.DeferUntil is not null && !(scheduled && decision.WriteOutbox)) {
        broken.Add($"{label}: a delivery time without a scheduled outbox copy");
      }
      if (policy.Decide(in situation) != decision) {
        broken.Add($"{label}: the decision is not deterministic");
      }
    }

    await Assert.That(broken).IsEmpty();
  }

  private static DispatchDecision _withoutReason(DispatchDecision decision) => decision with { Reason = default };
}
