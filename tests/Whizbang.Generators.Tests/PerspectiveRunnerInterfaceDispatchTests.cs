// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using static Whizbang.Generators.Tests.PerspectiveContractTestHarness;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generated runner calls each event's <c>Apply</c> through the interface that lists the event (#1936), a direct
/// interface call with nothing allocated, never through the perspective class by name. The interface is the contract
/// the compiler checks, so a perspective that implements <c>Apply</c> only explicitly is routed like any other, and the
/// method that runs is always the interface's implementation.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/PerspectiveRunnerGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class PerspectiveRunnerInterfaceDispatchTests {
  private const string PERSPECTIVES = "global::Whizbang.Core.Perspectives.";

  [RequiresAssemblyFiles]
  private static string _runner(string perspective, string runnerFile = "OrderProjectionRunner.g.cs") {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(PREAMBLE + perspective);
    return GeneratorTestHelper.GetGeneratedSource(result, runnerFile)!;
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EachEvent_IsAppliedThroughTheInterfaceThatListsItAsync() {
    var runner = _runner("""
        public class OrderProjection :
            IPerspectiveFor<Order, OrderPlaced>,
            IPerspectiveWithActionsFor<Order, OrderStatusChanged> {
          public Order Apply(Order current, OrderPlaced @event) => current;
          public ApplyResult<Order> Apply(Order current, OrderStatusChanged @event) => ModelAction.Purge;
        }
        """);

    await Assert.That(runner).Contains(
      $"return ((({PERSPECTIVES}IPerspectiveFor<global::TestApp.Order, global::TestApp.OrderPlaced>)perspective).Apply(currentModel!, typedEvent), global::Whizbang.Core.Perspectives.ModelAction.None);");
    await Assert.That(runner).Contains(
      $"var result_OrderStatusChanged = (({PERSPECTIVES}IPerspectiveWithActionsFor<global::TestApp.Order, global::TestApp.OrderStatusChanged>)perspective).Apply(currentModel!, typedEvent);");
    await Assert.That(runner).DoesNotContain("perspective.Apply(")
      .Because("nothing is routed through the class by the method's name");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AGlobalPerspective_IsAppliedThroughItsInterfaceAsync() {
    var runner = _runner("""
        public class OrdersByStatus : IGlobalPerspectiveFor<Order, string, OrderPlaced> {
          public string GetPartitionKey(OrderPlaced @event) => "all";
          public Order Apply(Order current, OrderPlaced @event) => current;
        }
        """, "OrdersByStatusRunner.g.cs");

    await Assert.That(runner).Contains(
      $"(({PERSPECTIVES}IGlobalPerspectiveFor<global::TestApp.Order, string, global::TestApp.OrderPlaced>)perspective).Apply(currentModel!, typedEvent)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task APerspectiveThatImplementsApplyOnlyExplicitly_GeneratesARunnerThatCompilesAsync() {
    // A consumer project has implicit usings; the generated runner relies on System.Linq being one of them.
    const string source = "global using System.Linq;\n" + PREAMBLE + """
        public class OrderProjection :
            IPerspectiveFor<Order, OrderPlaced>,
            IPerspectiveWithActionsFor<Order, OrderStatusChanged> {
          Order IPerspectiveFor<Order, OrderPlaced>.Apply(Order current, OrderPlaced @event) => current;
          ApplyResult<Order> IPerspectiveWithActionsFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) =>
            ModelAction.Purge;
        }
        """;

    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new PerspectiveRunnerGenerator(), source, "ExplicitApplyRunner");
    var errors = output.GetDiagnostics()
      .Where(static d => d.Severity == DiagnosticSeverity.Error)
      .Select(static d => d.ToString())
      .ToArray();

    await Assert.That(errors).IsEmpty();
  }

  /// <summary>
  /// What the runner reads off an <c>Apply</c> (<c>[MustExist]</c>, a body that can only purge) is read off the method
  /// that implements the interface, explicit or not, because that is the method that runs.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task MustExistAndAnUnconditionalPurge_AreReadOffTheExplicitImplementationAsync() {
    var runner = _runner("""
        public class OrderProjection :
            IPerspectiveFor<Order, OrderPlaced>,
            IPerspectiveWithActionsFor<Order, OrderStatusChanged> {
          [MustExist]
          Order IPerspectiveFor<Order, OrderPlaced>.Apply(Order current, OrderPlaced @event) => current;
          ApplyResult<Order> IPerspectiveWithActionsFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) =>
            ApplyResult<Order>.Purge();
        }
        """);

    await Assert.That(runner).Contains("Order must exist when applying OrderPlaced in OrderProjection");
    await Assert.That(runner).Contains("=> @event is global::TestApp.OrderStatusChanged")
      .Because("the unconditional purge is registered for the event whose explicit Apply can only purge");
  }
}
