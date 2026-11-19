// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using static Whizbang.Generators.Tests.PerspectiveContractTestHarness;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ113 (#1936): a method on a perspective with an Apply's signature and a name one slip away from <c>Apply</c> is a
/// handler that never runs. The rule is narrow on purpose, so an ordinary helper never trips it: the name is within one
/// edit of <c>Apply</c> (ignoring case), or starts with <c>Apply</c> or <c>Applie</c>, or ends with <c>Apply</c>; the parameters are the
/// perspective's model and an event; the return is an Apply's (the model, <c>ApplyResult</c>, <c>ModelAction</c> or the
/// tuple); and nothing in the perspective calls or references it.
/// </summary>
/// <tests>Whizbang.Generators/Analyzers/PerspectiveApplyNearMissAnalyzer.cs</tests>
[Category("Analyzers")]
public class PerspectiveApplyNearMissTests {
  private const string ID = "WHIZ113";

  private static string _perspectiveWith(string member) => PREAMBLE + $$"""
      public class OrderProjection : IPerspectiveFor<Order, OrderPlaced> {
        public Order Apply(Order current, OrderPlaced @event) => current;
        {{member}}
      }
      """;

  [Test]
  [Arguments("Aply")]
  [Arguments("Appy")]
  [Arguments("Aplpy")]
  [Arguments("apply")]
  [Arguments("Apply_")]
  [Arguments("Apply1")]
  [Arguments("ApplyEvent")]
  [Arguments("Applied")]
  [Arguments("ApplyingDiscount")]
  [Arguments("OnApply")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AMisspelledApplyThatNothingCalls_IsAnErrorAtTheMethodAsync(string name) {
    var source = _perspectiveWith($"public Order {name}(Order current, OrderStatusChanged @event) => current;");

    var reported = await ReportedAsync(source, ID);

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(LocatedText(reported[0])).IsEqualTo(name);
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains($"'{name}'");
    await Assert.That(message).Contains("OrderStatusChanged");
    await Assert.That(message).Contains("OrderProjection");
  }

  [Test]
  [Arguments("public ApplyResult<Order> Aply(Order current, OrderStatusChanged @event) => current;")]
  [Arguments("public ModelAction Aply(Order current, OrderStatusChanged @event) => ModelAction.Purge;")]
  [Arguments("public (Order?, ModelAction) Aply(Order current, OrderStatusChanged @event) => (null, ModelAction.Purge);")]
  [Arguments("public Order? Aply(Order current, OrderStatusChanged @event) => null;")]
  [Arguments("private Order Aply(Order current, OrderStatusChanged @event) => current;")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task EveryApplyReturnShape_AndAnyAccessibility_IsCoveredAsync(string member) {
    await Assert.That((await ReportedAsync(_perspectiveWith(member), ID)).Length).IsEqualTo(1);
  }

  /// <summary>Ordinary method names, including English words one or two letters from Apply, never trip the rule.</summary>
  [Test]
  [Arguments("Handle")]
  [Arguments("When")]
  [Arguments("Project")]
  [Arguments("Transform")]
  [Arguments("Evolve")]
  [Arguments("Supply")]
  [Arguments("Reply")]
  [Arguments("Copy")]
  [Arguments("Append")]
  [Arguments("Update")]
  [Arguments("Application")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task ALegitimateName_IsNotReportedAsync(string name) {
    var source = _perspectiveWith($"public Order {name}(Order current, OrderStatusChanged @event) => current;");

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty();
  }

  /// <summary>
  /// The name half of the rule, case by case: within one edit of Apply ignoring case (a letter inserted, removed or
  /// replaced, or two adjacent letters swapped), a name starting with Apply or Applie, or one ending with Apply.
  /// </summary>
  [Test]
  [Arguments("APPLY", true)]
  [Arguments("Aoply", true)]
  [Arguments("Applu", true)]
  [Arguments("Aplpy", true)]
  [Arguments("Aply", true)]
  [Arguments("Apply_Status", true)]
  [Arguments("Applies", true)]
  [Arguments("TryApply", true)]
  [Arguments("Reply", false)]
  [Arguments("Aplxy", false)]
  [Arguments("Aplpx", false)]
  [Arguments("When", false)]
  [Arguments("Handle", false)]
  [Arguments("Transform", false)]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task TheNameRule_DecidesExactlyTheseNamesAsync(string name, bool reported) {
    var source = _perspectiveWith($"public Order {name}(Order current, OrderStatusChanged @event) => current;");

    await Assert.That((await ReportedAsync(source, ID)).Length).IsEqualTo(reported ? 1 : 0);
  }

  [Test]
  [Arguments("public Order Aply(Order current, OrderLine line) => current;")]
  [Arguments("public Invoice Aply(Invoice current, OrderStatusChanged @event) => current;")]
  [Arguments("public string Aply(Order current, OrderStatusChanged @event) => \"\";")]
  [Arguments("public void Aply(Order current, OrderStatusChanged @event) { }")]
  [Arguments("public Order Aply(Order current) => current;")]
  [Arguments("public (Order?, string) Aply(Order current, OrderStatusChanged @event) => (null, \"\");")]
  [Arguments("public (Order?, ModelAction, int) Aply(Order current, OrderStatusChanged @event) => (null, ModelAction.None, 0);")]
  [Arguments("public ApplyResult<Invoice> Aply(Order current, OrderStatusChanged @event) => ModelAction.None;")]
  [Arguments("public static Order Aply(Order current, OrderStatusChanged @event) => current;")]
  [Arguments("public Order[] Aply(Order current, OrderStatusChanged @event) => [];")]
  [Arguments("public (Invoice?, ModelAction) Aply(Order current, OrderStatusChanged @event) => (null, ModelAction.None);")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AMethodWithoutAnApplysSignature_IsNotReported_WhateverItsNameAsync(string member) {
    await Assert.That(await ReportedAsync(_perspectiveWith(member), ID)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AHelperTheApplyCalls_IsNotReportedAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderStatusChanged> {
          public Order Apply(Order current, OrderStatusChanged @event) => ApplyStatus(current, @event);
          private Order ApplyStatus(Order current, OrderStatusChanged @event) => current with { Status = @event.Status };
        }
        """;

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AHelperReferencedAsAMethodGroup_IsNotReportedAsync() {
    const string source = PREAMBLE + """
        public class OrderProjection : IPerspectiveFor<Order, OrderStatusChanged> {
          private static readonly Func<OrderProjection, Func<Order, OrderStatusChanged, Order>> _step = static p => p.ApplyStatus;
          public Order Apply(Order current, OrderStatusChanged @event) => _step(this)(current, @event);
          private Order ApplyStatus(Order current, OrderStatusChanged @event) => current with { Status = @event.Status };
        }
        """;

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AMethodThatImplementsOrOverridesAContract_IsNotReportedAsync() {
    const string source = PREAMBLE + """
        public interface IOrderRules {
          Order ApplyRules(Order current, OrderStatusChanged @event);
        }

        public abstract class OrderBase : IPerspectiveFor<Order, OrderPlaced> {
          public Order Apply(Order current, OrderPlaced @event) => current;
          public abstract Order OnApply(Order current, OrderStatusChanged @event);
        }

        public class OrderProjection : OrderBase, IOrderRules {
          public Order ApplyRules(Order current, OrderStatusChanged @event) => current;
          public override Order OnApply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    var reported = await ReportedAsync(source, ID);

    await Assert.That(string.Join(",", reported.Select(LocatedText))).IsEqualTo("OnApply")
      .Because("the abstract declaration on the base is reported; the override and the interface implementation are contracts");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AClassThatIsNotAPerspective_IsNotReportedAsync() {
    const string source = PREAMBLE + """
        public class OrderRules {
          public Order Aply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AGlobalPerspective_IsCheckedAgainstItsModelAsync() {
    const string source = PREAMBLE + """
        public class OrdersByStatus : IGlobalPerspectiveFor<Order, string, OrderPlaced> {
          public string GetPartitionKey(OrderPlaced @event) => "all";
          public Order Apply(Order current, OrderPlaced @event) => current;
          public Order Aply(Order current, OrderStatusChanged @event) => current;
        }
        """;

    await Assert.That((await ReportedAsync(source, ID)).Length).IsEqualTo(1);
  }
}
