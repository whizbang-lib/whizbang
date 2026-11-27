// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using static Whizbang.Generators.Tests.PerspectiveContractTestHarness;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A fan-out's <c>Set</c> is folded per row on a rebuild against that row and the event alone, so it falls under the
/// purity rule collective specs follow (#2618): it depends on nothing but the row and the event (WHIZ106), and the
/// determinism checks every Apply has (WHIZ100 to WHIZ104) cover it too.
/// </summary>
/// <tests>Whizbang.Generators/PerspectivePurityAnalyzer.cs</tests>
[Category("Analyzers")]
[Category("FanOut")]
public class PerspectiveFanOutPurityTests {
  private const string FAN_OUT_PREAMBLE = """
      #nullable enable
      using System;
      using System.Linq.Expressions;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public sealed class OrderSummary {
        [PhysicalField] [Indexed] public Guid CustomerId { get; init; }
        public string CustomerName { get; init; } = "";
      }

      public sealed record CustomerRenamed(string Name) : IEvent;

      public sealed record Spec<T>(Expression<Action<ICollectiveSetters<T>>> Setters) : ICollectiveSpec<T> where T : class;

      public sealed class Holder {
        public string Value { get; set; } = "";
      }

      public abstract class ProjectionBase {
        public string BasePrefix { get; set; } = "";
      }

      public static class Names {
        public static string Current = "";
        public static readonly string Fixed = "fixed";
        public const string Constant = "constant";
        public static string Trim(string value) => value.Trim();
      }

      """;

  private static string _projection(string members, string body) => FAN_OUT_PREAMBLE + $$"""
      public class OrderSummaryProjection : ProjectionBase, IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
        {{members}}

        private static string Normalize(string value) => value.Trim();

        public static FanOutKey<OrderSummary> Targets => FanOut.By<OrderSummary>(o => o.CustomerId);

        public ICollectiveSpec<OrderSummary> Set(CustomerRenamed e) {
          {{body}}
        }
      }
      """;

  [Test]
  [Arguments("private readonly string _prefix = \"x\";", "return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, _prefix + e.Name));", "_prefix")]
  [Arguments("public string Prefix { get; set; } = \"\";", "return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, this.Prefix));", "this")]
  [Arguments("private string _format(string n) => n;", "return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, _format(e.Name)));", "_format")]
  [Arguments("", "return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, Names.Current));", "Names.Current")]
  [Arguments("private static string _last = \"\";", "return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, _last));", "_last")]
  [Arguments("public string Prefix { get; set; } = \"\";", "return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, Prefix));", "Prefix")]
  [Arguments("public event EventHandler? Changed;", "return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, Changed!.ToString()));", "Changed")]
  [Arguments("", "return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, BasePrefix));", "BasePrefix")]
  [Arguments("", "var text = base.ToString()!;\n    return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, text));", "base")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz106_ASetThatReadsStateBesidesTheRowAndTheEvent_IsAnErrorAsync(string members, string body, string located) {
    var reported = await ReportedAsync(_projection(members, body), "WHIZ106");

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(LocatedText(reported[0])).IsEqualTo(located);
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Set");
  }

  [Test]
  [Arguments("var name = Names.Trim(e.Name) + Names.Fixed + Names.Constant;\n    return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, name));")]
  [Arguments("return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, o => o.CustomerName + e.Name));")]
  [Arguments("string trim(string v) => v.Trim();\n    var name = trim(e.Name);\n    return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, name));")]
  [Arguments("return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, Normalize(e.Name)));")]
  [Arguments("var holder = new Holder { Value = e.Name };\n    return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, holder.Value));")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz106_ASetThatReadsOnlyTheRowTheEventAndConstants_IsNotReportedAsync(string body) {
    await Assert.That(await ReportedAsync(_projection("private readonly string _unused = \"\";", body), "WHIZ106")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz106_AnotherMethodOfTheProjectionThatReadsItsState_IsNotReportedAsync() {
    var source = _projection(
      "private readonly string _prefix = \"x\";\n  public string Describe() => _prefix;",
      "return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, e.Name));");

    await Assert.That(await ReportedAsync(source, "WHIZ106")).IsEmpty()
      .Because("only the Set a fan-out declares is folded on a rebuild");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz104_ASetThatReadsTheClock_IsReportedAsync() {
    var source = _projection("", "return new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, DateTime.UtcNow.ToString()));");

    var reported = await ReportedAsync(source, "WHIZ104");

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(LocatedText(reported[0])).IsEqualTo("DateTime.UtcNow");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz106_AnExplicitlyImplementedSet_IsCheckedAsync() {
    var source = FAN_OUT_PREAMBLE + """
        public class OrderSummaryProjection : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
          private readonly string _prefix = "x";

          public static FanOutKey<OrderSummary> Targets => FanOut.By<OrderSummary>(o => o.CustomerId);

          ICollectiveSpec<OrderSummary> IPerspectiveFanOutFor<OrderSummary, CustomerRenamed>.Set(CustomerRenamed e) =>
            new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, _prefix));
        }
        """;

    var reported = await ReportedAsync(source, "WHIZ106");

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(LocatedText(reported[0])).IsEqualTo("_prefix");
  }
}
