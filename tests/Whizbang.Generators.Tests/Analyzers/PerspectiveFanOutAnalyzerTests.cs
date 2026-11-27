// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using static Whizbang.Generators.Tests.PerspectiveContractTestHarness;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// The build-time rules of a fan-out (#2618): its <c>Targets</c> names one key property of the model (WHIZ124), the key
/// is a physical field (WHIZ125) with an index (WHIZ126) of a type that holds a stream id (WHIZ127), and no set-based
/// apply assigns it (WHIZ128). Each is reported on its own shape and not on the valid one.
/// </summary>
/// <tests>Whizbang.Generators/Analyzers/PerspectiveFanOutAnalyzer.cs</tests>
[Category("Analyzers")]
[Category("FanOut")]
public class PerspectiveFanOutAnalyzerTests {
  private static readonly string[] _fanOutIds = ["WHIZ124", "WHIZ125", "WHIZ126", "WHIZ127", "WHIZ128"];

  private const string FAN_OUT_PREAMBLE = """
      #nullable enable
      using System;
      using System.Linq.Expressions;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      [WhizbangId]
      public readonly partial struct CustomerKey;

      public sealed class Address {
        public Guid Id { get; init; }
      }

      public sealed class OrderSummary {
        [PhysicalField] [Indexed] public Guid CustomerId { get; init; }
        [PhysicalField] [Indexed] public Guid? OptionalCustomerId { get; init; }
        [PhysicalField] [Indexed] public CustomerKey TypedCustomerId { get; init; }
        [Indexed] public Guid DocumentOnlyId { get; init; }
        [PhysicalField] public Guid UnindexedId { get; init; }
        [PhysicalField] [Indexed] public string CustomerCode { get; init; } = "";
        public Address Address { get; init; } = new();
        public string CustomerName { get; init; } = "";
      }

      public sealed record CustomerRenamed(string Name) : IEvent;

      public sealed record Spec<T>(Expression<Action<ICollectiveSetters<T>>> Setters) : ICollectiveSpec<T> where T : class;

      """;

  private static string _fanOut(string targets, string setters = "s.SetProperty(o => o.CustomerName, e.Name)") =>
    FAN_OUT_PREAMBLE + $$"""
      public class OrderSummaryProjection : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
        private static readonly FanOutKey<OrderSummary> _cached = FanOut.By<OrderSummary>(o => o.CustomerId);

        public static FanOutKey<OrderSummary> Targets => {{targets}};

        public ICollectiveSpec<OrderSummary> Set(CustomerRenamed e) => new Spec<OrderSummary>(s => {{setters}});
      }
      """;

  [Test]
  [Arguments("FanOut.By<OrderSummary>(o => o.CustomerId)")]
  [Arguments("FanOut.By<OrderSummary>(o => o.OptionalCustomerId)")]
  [Arguments("FanOut.By<OrderSummary>(o => o.TypedCustomerId)")]
  [Arguments("FanOut.By<OrderSummary>((o) => o.CustomerId)")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AValidKey_ReportsNothingAsync(string targets) {
    foreach (var id in _fanOutIds) {
      await Assert.That(await ReportedAsync(_fanOut(targets), id)).IsEmpty().Because($"{id} must not fire on a valid key");
    }
  }

  [Test]
  [Arguments("_cached", "_cached")]
  [Arguments("FanOut.By<OrderSummary>(o => Guid.Empty)", "Guid.Empty")]
  [Arguments("FanOut.By<OrderSummary>(o => o.Address.Id)", "o.Address.Id")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz124_TargetsThatDoNotNameAKeyProperty_IsAnErrorAsync(string targets, string located) {
    var reported = await ReportedAsync(_fanOut(targets), "WHIZ124");

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(LocatedText(reported[0])).IsEqualTo(located);
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("OrderSummaryProjection");
  }

  [Test]
  [Arguments("DocumentOnlyId", "WHIZ125")]
  [Arguments("UnindexedId", "WHIZ126")]
  [Arguments("CustomerCode", "WHIZ127")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AKeyThatBreaksOneRule_IsReportedForThatRuleAloneAsync(string key, string id) {
    var source = _fanOut($"FanOut.By<OrderSummary>(o => o.{key})");

    foreach (var other in _fanOutIds.Where(i => i != id)) {
      await Assert.That(await ReportedAsync(source, other)).IsEmpty().Because($"{key} breaks only {id}");
    }
    var reported = await ReportedAsync(source, id);
    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(LocatedText(reported[0])).IsEqualTo($"o.{key}");
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains($"OrderSummary.{key}");
    await Assert.That(message).Contains("OrderSummaryProjection");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz125_ExplicitTargetsPerInterface_AreEachCheckedAsync() {
    const string source = FAN_OUT_PREAMBLE + """
        public sealed record CustomerDeleted : IEvent;

        public class OrderSummaryProjection
          : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed>,
            IPerspectiveFanOutFor<OrderSummary, CustomerDeleted> {
          static FanOutKey<OrderSummary> IPerspectiveFanOutFor<OrderSummary, CustomerRenamed>.Targets =>
            FanOut.By<OrderSummary>(o => o.CustomerId);

          static FanOutKey<OrderSummary> IPerspectiveFanOutFor<OrderSummary, CustomerDeleted>.Targets {
            get { return FanOut.By<OrderSummary>(o => o.DocumentOnlyId); }
          }

          public ICollectiveSpec<OrderSummary> Set(CustomerRenamed e) => new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, e.Name));
          public ICollectiveSpec<OrderSummary> Set(CustomerDeleted e) => new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, ""));
        }
        """;

    var reported = await ReportedAsync(source, "WHIZ125");

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(LocatedText(reported[0])).IsEqualTo("o.DocumentOnlyId");
  }

  [Test]
  [Arguments("{ get { var key = FanOut.By<OrderSummary>(o => o.CustomerId); return key; } }")]
  [Arguments("{ get; }")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz124_ATargetsThatDoesNotReturnTheKeyDirectly_IsAnErrorAsync(string accessors) {
    var source = FAN_OUT_PREAMBLE + $$"""
        public class OrderSummaryProjection : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
          public static FanOutKey<OrderSummary> Targets {{accessors}}

          public ICollectiveSpec<OrderSummary> Set(CustomerRenamed e) => new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, e.Name));
        }
        """;

    var reported = await ReportedAsync(source, "WHIZ124");

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(LocatedText(reported[0])).IsEqualTo("Targets");
  }

  [Test]
  [Arguments("public static FanOutKey<OrderSummary> Targets { get => FanOut.By<OrderSummary>(o => o.UnindexedId); }")]
  [Arguments("public static FanOutKey<OrderSummary> Targets { get { return FanOut.By<OrderSummary>(o => o.UnindexedId); } }")]
  [Arguments("public static FanOutKey<OrderSummary> Targets { get; } = FanOut.By<OrderSummary>(o => o.UnindexedId);")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task EveryFormThatReturnsTheKey_IsReadAsync(string targets) {
    var source = FAN_OUT_PREAMBLE + $$"""
        public class OrderSummaryProjection : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
          {{targets}}

          public ICollectiveSpec<OrderSummary> Set(CustomerRenamed e) => new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, e.Name));
        }
        """;

    var reported = await ReportedAsync(source, "WHIZ126");

    await Assert.That(reported.Length).IsEqualTo(1).Because("the key is read from each form, so its rules are checked");
    await Assert.That(LocatedText(reported[0])).IsEqualTo("o.UnindexedId");
  }

  [Test]
  [Arguments("public static FanOutKey<OrderSummary> Targets => Make(o => o.CustomerId);", "Make(o => o.CustomerId)")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz124_AKeyMadeByAnotherMethod_IsAnErrorAsync(string targets, string located) {
    var source = FAN_OUT_PREAMBLE + $$"""
        public class OrderSummaryProjection : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
          private static FanOutKey<OrderSummary> Make(Expression<Func<OrderSummary, object?>> key) => FanOut.By(key);

          {{targets}}

          public ICollectiveSpec<OrderSummary> Set(CustomerRenamed e) => new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, e.Name));
        }
        """;

    var reported = await ReportedAsync(source, "WHIZ124");

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(LocatedText(reported[0])).IsEqualTo(located);
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task APropertyNamedTargetsOnATypeWithNoFanOut_IsNotReadAsync() {
    var source = FAN_OUT_PREAMBLE + """
        public class Unrelated {
          public static object Targets => Guid.Empty;
        }
        """;

    await Assert.That(await ReportedAsync(source, "WHIZ124")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz128_ASetThatAssignsTheKey_IsAnErrorAsync() {
    var source = _fanOut("FanOut.By<OrderSummary>(o => o.CustomerId)", "s.SetProperty(o => o.CustomerId, Guid.Empty)");

    var reported = await ReportedAsync(source, "WHIZ128");

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(LocatedText(reported[0])).IsEqualTo("o => o.CustomerId");
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("OrderSummary.CustomerId");
    await Assert.That(message).Contains("OrderSummaryProjection");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz128_ACollectiveHandlerThatAssignsAFanOutKey_IsAnErrorAsync() {
    var source = _fanOut("FanOut.By<OrderSummary>(o => o.CustomerId)") + """

        public record MoveOrders : Whizbang.Core.Messaging.CollectiveEventBase { }

        public class OrderCollectives {
          [CollectiveApplyFor]
          public ICollectiveSpec<OrderSummary> Move(MoveOrders e) =>
            new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, "").SetProperty(o => o.CustomerId, Guid.Empty));
        }
        """;

    var reported = await ReportedAsync(source, "WHIZ128");

    await Assert.That(reported.Length).IsEqualTo(1)
      .Because("a key a collective changes would move the row away from the foreign events a rebuild finds by the key");
    await Assert.That(LocatedText(reported[0])).IsEqualTo("o => o.CustomerId");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task Whiz128_AssigningAPropertyOfAModelWithNoFanOutOnIt_IsNotReportedAsync() {
    var source = FAN_OUT_PREAMBLE + """
        public record MoveOrders : Whizbang.Core.Messaging.CollectiveEventBase { }

        public class OrderCollectives {
          [CollectiveApplyFor]
          public ICollectiveSpec<OrderSummary> Move(MoveOrders e) =>
            new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerId, o => o.CustomerId));
        }
        """;

    await Assert.That(await ReportedAsync(source, "WHIZ128")).IsEmpty()
      .Because("CustomerId is a key only where a fan-out declares it, and here none does");
  }
}
