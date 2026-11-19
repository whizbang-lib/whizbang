// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using static Whizbang.Generators.Tests.PerspectiveContractTestHarness;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ117 (#1936): an event a perspective lists only through a custom marker interface that extends
/// <c>IPerspectiveBase</c> without declaring an <c>Apply</c> is counted as routed, yet no interface-declared
/// <c>Apply</c> handles it, so nothing strongly typed does. It is an error until custom markers are supported.
/// </summary>
/// <tests>Whizbang.Generators/Analyzers/PerspectiveContractAnalyzer.cs</tests>
[Category("Analyzers")]
public class PerspectiveMarkerOnlyEventTests {
  private const string ID = "WHIZ117";

  private const string MARKER = """
      public interface IOrderMarker<TModel, TEvent> : IPerspectiveBase<TModel, TEvent>
        where TModel : class
        where TEvent : IEvent;

      """;

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnEventListedOnlyByAMarkerWithNoApply_IsAnErrorAtThePerspectiveAsync() {
    const string source = PREAMBLE + MARKER + """
        public class OrderProjection : IPerspectiveFor<Order, OrderPlaced>, IOrderMarker<Order, OrderStatusChanged> {
          public Order Apply(Order current, OrderPlaced @event) => current;
        }
        """;

    var reported = await ReportedAsync(source, ID);

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(LocatedText(reported[0])).IsEqualTo("OrderProjection");
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("OrderStatusChanged");
    await Assert.That(message).Contains("IPerspectiveFor<Order");
    await Assert.That(message).Contains("IPerspectiveWithActionsFor<Order");
    await Assert.That(message).Contains("IGlobalPerspectiveFor<Order");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnEventListedByIPerspectiveBaseDirectly_IsAnErrorAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveBase<Order, OrderStatusChanged> {
          public Order Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That((await ReportedAsync(source, ID)).Length).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnEventAMarkerAndADeclaringInterfaceBothList_IsNotReportedAsync() {
    const string source = PREAMBLE + MARKER + """
        public class OrderProjection : IPerspectiveFor<Order, OrderPlaced>, IOrderMarker<Order, OrderPlaced> {
          public Order Apply(Order current, OrderPlaced @event) => current;
        }

        public class OrdersByStatus : IGlobalPerspectiveFor<Order, string, OrderPlaced> {
          public string GetPartitionKey(OrderPlaced @event) => "all";
          public Order Apply(Order current, OrderPlaced @event) => current;
        }

        public class OrderLifecycle : IPerspectiveWithActionsFor<Order, OrderStatusChanged> {
          public ApplyResult<Order> Apply(Order current, OrderStatusChanged @event) => ModelAction.Purge;
        }
        """;

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty();
  }
}
