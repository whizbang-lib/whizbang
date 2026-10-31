// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using static Whizbang.Generators.Tests.PerspectiveContractTestHarness;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ114 (#1936): the runner calls <c>Apply</c> through the interface that lists the event, so when a perspective has
/// an explicit interface <c>Apply</c> and another <c>Apply</c> with the same parameters, the explicit one runs and the
/// other never does, whatever either returns. The build reports the one that never runs.
/// </summary>
/// <tests>Whizbang.Generators/Analyzers/PerspectiveContractAnalyzer.cs</tests>
[Category("Analyzers")]
public class PerspectiveDuplicateApplyTests {
  private const string ID = "WHIZ114";

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task TheApplyBesideAnExplicitInterfaceApply_IsAnErrorAtTheOneThatNeverRunsAsync() {
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
      .IsEqualTo(source.Split('\n').ToList().FindIndex(static l => l.Contains("public ModelAction Apply")));
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).IsEqualTo(
      "'PermissionProjection' has an explicit interface Apply and another Apply for 'OrderStatusChanged'. The runner calls "
      + "the interface's implementation ('IPerspectiveFor<Order, OrderPlaced, OrderStatusChanged>.Apply'), so "
      + "'ModelAction Apply(Order, OrderStatusChanged)' never runs. Keep one Apply per event.");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task TheApplyBesideAnExplicitInterfaceApply_NeverRuns_WhateverItReturnsAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderStatusChanged> {
          public Order Apply(Order current, OrderStatusChanged @event) => current with { Status = @event.Status };
          Order IPerspectiveFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That((await ReportedAsync(source, ID)).Length).IsEqualTo(1)
      .Because("the runner calls the interface's implementation, so the other Apply never runs whatever it returns");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnInheritedApplyBesideAnExplicitInterfaceApply_IsAnErrorAtThePerspectiveAsync() {
    const string source = PREAMBLE + """
        public class OrderHandlers {
          public ModelAction Apply(Order current, OrderStatusChanged @event) => ModelAction.Purge;
        }

        public class OrderProjection : OrderHandlers, IPerspectiveFor<Order, OrderStatusChanged> {
          Order IPerspectiveFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    var reported = await ReportedAsync(source, ID);

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(LocatedText(reported[0])).IsEqualTo("OrderProjection")
      .Because("the Apply that never runs is declared on another type; the perspective that inherits it is what has two");
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("(inherited from 'OrderHandlers')");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnExplicitInterfaceApplyAlone_IsNotReportedAsync() {
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
