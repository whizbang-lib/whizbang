// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The typed appliers <c>GetPerspectiveAssociations&lt;TModel, TEvent&gt;()</c> hands out call a perspective's
/// <c>Apply</c> through the interface that lists the event (#1936), so the result is the interface's declared return.
/// Calling the class's own <c>Apply</c> instead bound whichever overload the class exposed for the event, and a class
/// whose ordinary <c>Apply</c> returned a <c>ModelAction</c> next to an explicit <c>TModel</c> implementation made the
/// applier cast the boxed action to the model and throw <c>InvalidCastException</c>. Each test compiles a perspective
/// and a probe that runs the generated applier, and reads what it returned.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/PerspectiveDiscoveryGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class PerspectiveAssociationApplierTests {
  private const string CONTRACTS = """
      public record Order {
        [StreamId] public Guid Id { get; init; }
        public string Status { get; init; } = "";
      }

      public record OrderStatusChanged : IEvent {
        [StreamId] public Guid OrderId { get; init; }
        public string Status { get; init; } = "";
      }
      """;

  [RequiresAssemblyFiles]
  private static string _runProbe(string assemblyName, string perspective) {
    var source = $$"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      namespace App {
        {{CONTRACTS}}
        {{perspective}}
        public static class Probe {
          public static string Run() {
            var applier = global::{{assemblyName}}.Generated.PerspectiveRegistrationExtensions
              .GetPerspectiveAssociations<Order, OrderStatusChanged>("{{assemblyName}}")[0].ApplyDelegate;
            try {
              return applier(new Order { Status = "before" }, new OrderStatusChanged { Status = "after" }).Status;
            } catch (InvalidCastException) {
              return "InvalidCastException";
            }
          }
        }
      }
      """;
    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new PerspectiveDiscoveryGenerator(), source, assemblyName);
    using var image = new MemoryStream();
    var emitted = output.Emit(image);
    if (!emitted.Success) {
      throw new InvalidOperationException(
        "The probe does not compile: "
        + string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
    var assembly = System.Reflection.Assembly.Load(image.ToArray());
    var run = assembly.GetType("App.Probe", throwOnError: true)!.GetMethod("Run")!;
    return (string)run.Invoke(null, null)!;
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TheApplier_RunsTheIPerspectiveForImplementation_NotAnOrdinaryApplyThatReturnsAnActionAsync() {
    const string perspective = """
      public class OrderProjection : IPerspectiveFor<Order, OrderStatusChanged> {
        public ModelAction Apply(Order current, OrderStatusChanged @event) => ModelAction.Purge;
        Order IPerspectiveFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) =>
          current with { Status = @event.Status };
      }
      """;

    await Assert.That(_runProbe("ApplierDecoyModel", perspective)).IsEqualTo("after")
      .Because("the applier calls the interface member, whose declared return is the model, and casts nothing blindly");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TheApplier_RunsTheIPerspectiveWithActionsForImplementation_NotAnOrdinaryApplyThatReturnsTheModelAsync() {
    const string perspective = """
      public class OrderProjection : IPerspectiveWithActionsFor<Order, OrderStatusChanged> {
        public Order Apply(Order current, OrderStatusChanged @event) => current;
        ApplyResult<Order> IPerspectiveWithActionsFor<Order, OrderStatusChanged>.Apply(Order current, OrderStatusChanged @event) =>
          current with { Status = @event.Status };
      }
      """;

    await Assert.That(_runProbe("ApplierDecoyActions", perspective)).IsEqualTo("after");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TheApplier_RunsAnOrdinaryInterfaceImplementationAsBeforeAsync() {
    const string perspective = """
      public class OrderProjection : IPerspectiveFor<Order, OrderStatusChanged> {
        public Order Apply(Order current, OrderStatusChanged @event) => current with { Status = @event.Status };
      }
      """;

    await Assert.That(_runProbe("ApplierOrdinary", perspective)).IsEqualTo("after");
  }
}
