// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// What a perspective's <c>Apply</c> may assume about the current model it is handed: it is never null (#1878).
/// </summary>
/// <tests>Whizbang.Generators/PerspectiveCurrentModelAnalyzer.cs</tests>
[Category("Analyzers")]
public class PerspectiveCurrentModelAnalyzerTests {
  private const string PREAMBLE = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record Order {
        [StreamId] public Guid Id { get; init; }
        public string? Name { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
      }

      public record OrderPlaced : IEvent {
        [StreamId] public Guid OrderId { get; init; }
        public DateTimeOffset At { get; init; }
      }

      public record OrderRenamed : IEvent {
        [StreamId] public Guid OrderId { get; init; }
        public string Name { get; init; } = "";
      }

      """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnApplyThatTestsItsCurrentModelForNull_IsWarnedThatItNeverIsAsync() {
    const string source = PREAMBLE + """
        public class OrderPerspective : IPerspectiveFor<Order, OrderPlaced> {
          public Order Apply(Order current, OrderPlaced @event) =>
            current is null ? new Order { Id = @event.OrderId, CreatedAt = @event.At } : current;
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveCurrentModelAnalyzer>(source);

    var whiz107 = diagnostics.Where(d => d.Id == "WHIZ107").ToArray();
    await Assert.That(whiz107.Length).IsEqualTo(1);
    await Assert.That(whiz107[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    var message = whiz107[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("current");
    await Assert.That(message).Contains("ApplyContext.Current.IsFirstEventInStream")
      .Because("the warning points at the deterministic fact that replaces the null check (#1878)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EveryFormOfTheNullCheck_IsWarnedAsync() {
    const string source = PREAMBLE + """
        public class OrderPerspective : IPerspectiveFor<Order, OrderPlaced, OrderRenamed> {
          public Order Apply(Order current, OrderPlaced @event) {
            if (current == null) { return new Order { Id = @event.OrderId }; }
            if (null == current) { return new Order { Id = @event.OrderId }; }
            return current is not null ? current : new Order();
          }

          public Order Apply(Order current, OrderRenamed @event) =>
            current != null ? current with { Name = @event.Name } : new Order { Name = @event.Name };
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveCurrentModelAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ107")).IsEqualTo(4)
      .Because("== null, null ==, is not null and != null each test the model for a null it never is");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ANullCheckOfAPropertyOrOfTheEvent_IsNotWarnedAsync() {
    const string source = PREAMBLE + """
        public class OrderPerspective : IPerspectiveFor<Order, OrderRenamed> {
          public Order Apply(Order current, OrderRenamed @event) =>
            current.Name is null || @event is null || @event.Name == null || null == @event.Name
              ? current with { Name = "unnamed" }
              : current with { Name = @event.Name };
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveCurrentModelAnalyzer>(source);

    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ107")).IsFalse()
      .Because("only the model itself is never null; its properties and the event are the Apply's to test");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ANullCheckOutsideAPerspectiveApply_IsNotWarnedAsync() {
    const string source = PREAMBLE + """
        public class NotAPerspective {
          public Order Apply(Order current, OrderPlaced @event) => current is null ? new Order() : current;
        }

        public class OrderPerspective : IPerspectiveFor<Order, OrderPlaced> {
          public Order Apply(Order current, OrderPlaced @event) => current with { Id = @event.OrderId };

          public static Order Merge(Order current) => current is null ? new Order() : current;

          public static Order Apply(Order current) => current is null ? new Order() : current;
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveCurrentModelAnalyzer>(source);

    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ107")).IsFalse()
      .Because("the runner's no-null contract covers a perspective's Apply, and nothing else");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ATestOfTheModelAgainstSomethingOtherThanNull_IsNotWarnedAsync() {
    const string source = PREAMBLE + """
        public class OrderPerspective : IPerspectiveFor<Order, OrderPlaced> {
          private static readonly Order _blank = new();
          public Order Apply(Order current, OrderPlaced @event) =>
            current == _blank || current is { Name: null } || current.Name is "draft" || current.Name is not "final"
              ? current with { Id = @event.OrderId }
              : current;
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveCurrentModelAnalyzer>(source);

    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ107")).IsFalse();
  }
}
