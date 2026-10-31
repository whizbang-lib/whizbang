// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using static Whizbang.Generators.Tests.PerspectiveContractTestHarness;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ115 (#1936): an <c>Apply</c> for a routed event returns what the interface that lists the event declares,
/// <c>TModel</c> for <c>IPerspectiveFor</c> and <c>ApplyResult&lt;TModel&gt;</c> for <c>IPerspectiveWithActionsFor</c>.
/// Any other shape (bare <c>ModelAction</c>, the <c>(TModel?, ModelAction)</c> tuple, the other family's return) can
/// only compile next to an explicit interface implementation that never runs, so it is an error that names the fix.
/// </summary>
/// <tests>Whizbang.Generators/Analyzers/PerspectiveContractAnalyzer.cs</tests>
[Category("Analyzers")]
public class PerspectiveApplyReturnShapeTests {
  private const string ID = "WHIZ115";

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task ABareModelActionReturn_ForAnIPerspectiveForEvent_IsAnErrorNamingTheFixAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderPlaced, OrderStatusChanged> {
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
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("OrderStatusChanged");
    await Assert.That(message).Contains("ModelAction");
    await Assert.That(message).Contains("IPerspectiveWithActionsFor<Order");
    await Assert.That(message).Contains("ApplyResult<Order>");
    await Assert.That(message).Contains("return ModelAction.Purge;")
      .Because("the fix keeps the one-line purge: ModelAction converts to ApplyResult<TModel> implicitly");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task TheTupleReturn_IsAnErrorAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderStatusChanged> {
          public (Order?, ModelAction) Apply(Order current, OrderStatusChanged @event) => (null, ModelAction.Purge);
          Order IPerspectiveFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That((await ReportedAsync(source, ID)).Length).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task TheOtherFamilysReturn_IsAnError_InEitherDirectionAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection :
            IPerspectiveFor<Order, OrderPlaced>,
            IPerspectiveWithActionsFor<Order, OrderStatusChanged> {
          public ApplyResult<Order> Apply(Order current, OrderPlaced @event) => current;
          Order IPerspectiveFor<Order, OrderPlaced>.Apply(Order current, OrderPlaced @event) => current;

          public Order Apply(Order current, OrderStatusChanged @event) => current;
          ApplyResult<Order> IPerspectiveWithActionsFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That((await ReportedAsync(source, ID)).Length).IsEqualTo(2);
  }

  /// <summary>
  /// A <c>TModel?</c> return implements <c>TModel Apply</c>: nullability does not take part in matching an interface
  /// member (the compiler warns, CS8766, and accepts it), so it is the interface's shape and is not reported.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task ANullableModelReturn_ImplementsTheInterface_AndIsNotReportedAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderStatusChanged> {
          public Order? Apply(Order current, OrderStatusChanged @event) => null;
        }
        """;

    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source);
    var projection = compilation.GetTypeByMetadataName("TestApp.OrderProjection")!;
    var declared = projection.AllInterfaces.SelectMany(static i => i.GetMembers("Apply")).Single();
    await Assert.That(projection.FindImplementationForInterfaceMember(declared))
      .IsEqualTo(projection.GetMembers("Apply").Single(), SymbolEqualityComparer.Default)
      .Because("the compiler accepts the nullable return as the interface's implementation");
    await Assert.That(await ReportedAsync(source, ID)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnEventListedByBothFamilies_AcceptsEitherDeclaredReturnAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection :
            IPerspectiveFor<Order, OrderStatusChanged>,
            IPerspectiveWithActionsFor<Order, OrderStatusChanged> {
          public Order Apply(Order current, OrderStatusChanged @event) => current;
          ApplyResult<Order> IPerspectiveWithActionsFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty()
      .Because("the return matches an interface that lists the event; the explicit one is WHIZ114's to report");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnInheritedNonInterfaceReturn_IsAnErrorAtTheDerivedPerspectiveAsync() {
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
    await Assert.That(LocatedText(reported[0])).IsEqualTo("OrderProjection");
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("OrderHandlers");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnEventListedOnlyByAMarkerWithNoApplyMember_HasNoDeclaredShapeToJudgeAsync() {
    const string source = PREAMBLE + """
        public interface IOrderMarker<TModel, TEvent> : IPerspectiveBase<TModel, TEvent>
          where TModel : class
          where TEvent : IEvent;

        public class OrderProjection : IPerspectiveFor<Order, OrderPlaced>, IOrderMarker<Order, OrderStatusChanged> {
          public Order Apply(Order current, OrderPlaced @event) => current;
          public ModelAction Apply(Order current, OrderStatusChanged @event) => ModelAction.Purge;
        }
        """;

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty();
  }

  /// <summary>
  /// A shape only a suppressed WHIZ115 allows is never what runs: the runner calls Apply through the interface that
  /// lists the event (#1936), so the interface's implementation runs and the class's own method is ignored.
  /// </summary>
  [Test]
  [Arguments("public ModelAction Apply(Order current, OrderStatusChanged @event) => ModelAction.Purge;")]
  [Arguments("public (Order?, ModelAction) Apply(Order current, OrderStatusChanged @event) => (null, ModelAction.Purge);")]
  [RequiresAssemblyFiles]
  public async Task AShapeOnlyASuppressedWhiz115Allows_IsNeverWhatTheRunnerCallsAsync(string apply) {
    var source = PREAMBLE + $$"""
        public class OrderProjection : IPerspectiveFor<Order, OrderStatusChanged> {
          {{apply}}
          Order IPerspectiveFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "OrderProjectionRunner.g.cs");

    await Assert.That(runner).Contains(
      "return (((global::Whizbang.Core.Perspectives.IPerspectiveFor<global::TestApp.Order, global::TestApp.OrderStatusChanged>)perspective).Apply(currentModel!, typedEvent), global::Whizbang.Core.Perspectives.ModelAction.None);");
  }
}
