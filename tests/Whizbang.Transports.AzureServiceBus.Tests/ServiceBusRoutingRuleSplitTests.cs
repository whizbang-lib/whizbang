// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// #2089: routing-pattern clauses split into rules that each fit the broker's SQL filter limit.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/ServiceBusEntityProvisioning.cs</code-under-test>
public class ServiceBusRoutingRuleSplitTests {
  [Test]
  public async Task NoClauses_NoRulesAsync() {
    await Assert.That(ServiceBusEntityProvisioning.SplitIntoRules([])).IsEmpty();
  }

  [Test]
  public async Task FewClauses_OneRuleUnderTheHistoricalNameAsync() {
    var rules = ServiceBusEntityProvisioning.SplitIntoRules(["sys.Label LIKE 'a.%'", "sys.Label LIKE 'b.%'"]);

    await Assert.That(rules.Count).IsEqualTo(1);
    await Assert.That(rules[0].Name).IsEqualTo(ServiceBusEntityProvisioning.ROUTING_PATTERN_RULE_NAME);
    await Assert.That(rules[0].Expression).IsEqualTo("sys.Label LIKE 'a.%' OR sys.Label LIKE 'b.%'");
  }

  [Test]
  public async Task AClauseLongerThanTheLimit_StillGetsARuleOfItsOwnAsync() {
    var huge = "sys.Label LIKE '" + new string('x', ServiceBusEntityProvisioning.MAX_SQL_FILTER_LENGTH) + "'";

    var rules = ServiceBusEntityProvisioning.SplitIntoRules(["sys.Label LIKE 'a.%'", huge, "sys.Label LIKE 'b.%'"]);

    await Assert.That(rules.Select(r => r.Expression)).IsEquivalentTo(["sys.Label LIKE 'a.%'", huge, "sys.Label LIKE 'b.%'"])
      .Because("a pattern never silently disappears; the broker refusing an oversized rule is logged");
    await Assert.That(rules[2].Name).IsEqualTo(ServiceBusEntityProvisioning.ROUTING_PATTERN_RULE_NAME + "-3");
  }
}
