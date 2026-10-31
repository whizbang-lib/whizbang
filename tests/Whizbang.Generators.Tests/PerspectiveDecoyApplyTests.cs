// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using static Whizbang.Generators.Tests.PerspectiveContractTestHarness;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ114 (#1936): the runner calls the perspective's own <c>Apply</c> for an event, so an explicit interface
/// <c>Apply</c> next to an ordinary <c>Apply</c> with the same parameters is a decoy that never runs, whatever either
/// returns, and the build says so.
/// </summary>
/// <tests>Whizbang.Generators/Analyzers/PerspectiveContractAnalyzer.cs</tests>
[Category("Analyzers")]
public class PerspectiveDecoyApplyTests {
  private const string ID = "WHIZ114";

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnExplicitApplyHiddenByAnOrdinaryApply_IsAnErrorAtTheExplicitOneAsync() {
    const string source = PREAMBLE + """
        public class PermissionProjection : IPerspectiveFor<Order, OrderPlaced, OrderStatusChanged> {
          public Order Apply(Order current, OrderPlaced @event) => current;
          public ModelAction Apply(Order current, OrderStatusChanged @event) => ModelAction.Purge;
          Order IPerspectiveFor<Order, OrderPlaced, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    var reported = await ReportedAsync(source, ID);

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(LocatedText(reported[0])).IsEqualTo("Apply");
    await Assert.That(reported[0].Location.GetLineSpan().StartLinePosition.Line)
      .IsEqualTo(source.Split('\n').ToList().FindIndex(static l => l.Contains("Order IPerspectiveFor<Order, OrderPlaced, OrderStatusChanged>.Apply")));
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("OrderStatusChanged");
    await Assert.That(message).Contains("PermissionProjection");
    await Assert.That(message).Contains("never runs");
    await Assert.That(message).Contains("ModelAction");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnExplicitApplyHiddenByAnOrdinaryApplyWithTheSameReturn_IsStillADecoyAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderStatusChanged> {
          public Order Apply(Order current, OrderStatusChanged @event) => current with { Status = @event.Status };
          Order IPerspectiveFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That((await ReportedAsync(source, ID)).Length).IsEqualTo(1)
      .Because("the runner calls the ordinary Apply, so the explicit one never runs whatever it returns");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnExplicitApplyHiddenByAnInheritedOrdinaryApply_IsADecoyAsync() {
    const string source = PREAMBLE + """
        public class OrderHandlers {
          public ModelAction Apply(Order current, OrderStatusChanged @event) => ModelAction.Purge;
        }

        public class OrderProjection : OrderHandlers, IPerspectiveFor<Order, OrderStatusChanged> {
          Order IPerspectiveFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That((await ReportedAsync(source, ID)).Length).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnExplicitImplementationWithNoOrdinaryCounterpart_IsNotADecoyAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderStatusChanged> {
          Order IPerspectiveFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) => current;
          public Order Apply(Order current, OrderPlaced @event) => current;
        }

        public class OrdersByStatus : IGlobalPerspectiveFor<Order, string, OrderPlaced> {
          string IGlobalPerspectiveFor<Order, string, OrderPlaced>.GetPartitionKey(OrderPlaced @event) => "all";
          public string GetPartitionKey(OrderUpdated @event) => "all";
          public Order Apply(Order current, OrderPlaced @event) => current;
        }
        """;

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty();
  }
}
