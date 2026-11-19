// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using static Whizbang.Generators.Tests.PerspectiveContractTestHarness;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ109 (#1936): the perspective interfaces are the contract the runner routes by, so an <c>Apply(TModel, E)</c> whose
/// event no interface lists for that model never runs, and the build says so instead of dropping it silently.
/// </summary>
/// <tests>Whizbang.Generators/Analyzers/PerspectiveContractAnalyzer.cs</tests>
[Category("Analyzers")]
public class PerspectiveUnlistedApplyTests {
  private const string ID = "WHIZ109";

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnApplyForAnEventNoInterfaceLists_IsAnErrorAtTheMethodAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderPlaced, OrderUpdated> {
          public Order Apply(Order current, OrderPlaced @event) => current;
          public Order Apply(Order current, OrderUpdated @event) => current;
          public (Order?, ModelAction) Apply(Order current, OrderStatusChanged @event) => (current, ModelAction.None);
        }
        """;

    var reported = await ReportedAsync(source, ID);

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(LocatedText(reported[0])).IsEqualTo("Apply");
    await Assert.That(reported[0].Location.GetLineSpan().StartLinePosition.Line)
      .IsEqualTo(source.Split('\n').ToList().FindIndex(static l => l.Contains("OrderStatusChanged @event")));
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("OrderStatusChanged");
    await Assert.That(message).Contains("OrderProjection");
    await Assert.That(message).Contains("IPerspectiveFor<Order");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnApplyForAListedEvent_IsNotReported_WhicheverInterfaceListsItAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection :
            IPerspectiveFor<Order, OrderPlaced>,
            IPerspectiveFor<Order, OrderUpdated>,
            IPerspectiveWithActionsFor<Order, OrderStatusChanged> {
          public Order Apply(Order current, OrderPlaced @event) => current;
          public Order Apply(Order current, OrderUpdated @event) => current;
          public ApplyResult<Order> Apply(Order current, OrderStatusChanged @event) => ModelAction.Purge;
        }
        """;

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty()
      .Because("one class may implement either interface any number of times and group its events freely");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnUnlistedApply_IsAnError_WhateverItsAccessibilityAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderPlaced> {
          public Order Apply(Order current, OrderPlaced @event) => current;
          private Order Apply(Order current, OrderUpdated @event) => current;
          internal Order Apply(Order current, OrderStatusChanged @event) => current;
          protected Order Apply(Order current, InvoiceIssued @event) => current;
        }
        """;

    await Assert.That((await ReportedAsync(source, ID)).Length).IsEqualTo(3);
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnInheritedUnlistedApply_IsAnErrorAtTheDerivedPerspectiveAsync() {
    const string source = PREAMBLE + """
        public class OrderHandlers {
          public Order Apply(Order current, OrderStatusChanged @event) => current with { Status = @event.Status };
        }

        public class OrderProjection : OrderHandlers, IPerspectiveFor<Order, OrderPlaced> {
          public Order Apply(Order current, OrderPlaced @event) => current;
        }
        """;

    var reported = await ReportedAsync(source, ID);

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(LocatedText(reported[0])).IsEqualTo("OrderProjection")
      .Because("the inherited method is declared elsewhere; the perspective that inherits it is what fails to route it");
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("OrderHandlers");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnOverriddenUnlistedApply_IsReportedOncePerPerspectiveAsync() {
    const string source = PREAMBLE + """
        public abstract class OrderBase : IPerspectiveFor<Order, OrderPlaced> {
          public Order Apply(Order current, OrderPlaced @event) => current;
          public virtual Order Apply(Order current, OrderStatusChanged @event) => current;
        }

        public class OrderProjection : OrderBase {
          public override Order Apply(Order current, OrderStatusChanged @event) => current with { Status = @event.Status };
        }
        """;

    var reported = await ReportedAsync(source, ID);

    await Assert.That(string.Join(",", reported.Select(LocatedText)))
      .IsEqualTo("Apply,Apply")
      .Because("the base reports its own method and the derived its override, and the derived does not report the base's again");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnApplyForAnEventListedOnlyForAnotherModel_IsAnErrorAsync() {
    const string source = PREAMBLE + """
        public class OrderAndInvoiceProjection : IPerspectiveFor<Order, OrderPlaced>, IPerspectiveFor<Invoice, InvoiceIssued> {
          public Order Apply(Order current, OrderPlaced @event) => current;
          public Invoice Apply(Invoice current, InvoiceIssued @event) => current;
          public Order Apply(Order current, InvoiceIssued @event) => current;
        }
        """;

    var reported = await ReportedAsync(source, ID);

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("InvoiceIssued");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AGlobalPerspective_IsHeldToTheEventsItsInterfaceListsAsync() {
    const string source = PREAMBLE + """
        public class OrdersByStatus : IGlobalPerspectiveFor<Order, string, OrderPlaced> {
          public string GetPartitionKey(OrderPlaced @event) => "all";
          public Order Apply(Order current, OrderPlaced @event) => current;
          public Order Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That((await ReportedAsync(source, ID)).Length).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task MethodsThatCannotBeAnApply_AreNotReportedAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderPlaced> {
          public Order Apply(Order current, OrderPlaced @event) => current;
          // Not an event: a helper taking a value.
          public Order Apply(Order current, OrderLine line) => current;
          // Not the model.
          public Invoice Apply(Invoice current, OrderStatusChanged @event) => current;
          // Not two parameters.
          public Order Apply(Order current, OrderStatusChanged @event, int times) => current;
          // Static: the runner calls an instance.
          public static Order Apply(OrderUpdated @event, Order current) => current;
          public static Order Apply(Order current, OrderUpdated @event, bool _) => current;
        }

        public class NotAPerspective {
          public Order Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AStaticApplyForAnUnlistedEvent_IsNotReported_BecauseOnlyInstanceMethodsAreHandlersAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderPlaced> {
          public Order Apply(Order current, OrderPlaced @event) => current;
          public static Order Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty();
  }
}
