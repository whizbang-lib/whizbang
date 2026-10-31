// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using static Whizbang.Generators.Tests.PerspectiveContractTestHarness;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ116 (#1936): a collective handler is bound by <c>CollectiveApplyShape</c>, and a method that is meant to be one
/// (it carries <c>[CollectiveApplyFor]</c>, or it implements <c>ICollectiveApplyFor&lt;TModel, TEvent&gt;.Apply</c>) but
/// does not match the shape used to be left out of the registry without a word. Now the build names what is wrong.
/// </summary>
/// <tests>Whizbang.Generators/Analyzers/CollectiveApplyShapeAnalyzer.cs</tests>
[Category("Analyzers")]
[Category("CollectiveEvents")]
public class CollectiveApplyShapeAnalyzerTests {
  private const string ID = "WHIZ116";

  private const string COLLECTIVE_PREAMBLE = PREAMBLE + """
      public record ArchiveOrders : Whizbang.Core.Messaging.CollectiveEventBase { }

      """;

  private static string _handlerWith(string member) => COLLECTIVE_PREAMBLE + $$"""
      public class OrderCollectives {
        {{member}}
      }
      """;

  [Test]
  [Arguments("[CollectiveApplyFor] public ICollectiveSpec<Order> Apply(ArchiveOrders e) => null!;")]
  [Arguments("[CollectiveApplyFor] public ICollectiveSpec<Order> Apply(ArchiveOrders e, ICollectiveQuery query) => null!;")]
  [Arguments("public ICollectiveSpec<Order> Apply(OrderPlaced e) => null!;")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AHandlerThatMatchesTheShape_OrAMethodThatIsNotOne_IsNotReportedAsync(string member) {
    await Assert.That(await ReportedAsync(_handlerWith(member), ID)).IsEmpty();
  }

  [Test]
  [Arguments("[CollectiveApplyFor] public Order Apply(ArchiveOrders e) => null!;", "ICollectiveSpec<TModel>")]
  [Arguments("[CollectiveApplyFor] public ICollectiveSpec<Order> Apply() => null!;", "takes 0 parameters")]
  [Arguments("[CollectiveApplyFor] public ICollectiveSpec<Order> Apply(ArchiveOrders e, ICollectiveQuery q, int n) => null!;", "takes 3 parameters")]
  [Arguments("[CollectiveApplyFor] public ICollectiveSpec<Order> Apply(OrderPlaced e) => null!;", "ICollectiveEvent")]
  [Arguments("[CollectiveApplyFor] public ICollectiveSpec<Order> Apply(ArchiveOrders e, string q) => null!;", "ICollectiveQuery")]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnAttributedMethodThatDoesNotMatchTheShape_IsAnErrorNamingWhatIsWrongAsync(string member, string named) {
    var reported = await ReportedAsync(_handlerWith(member), ID);

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(LocatedText(reported[0])).IsEqualTo("Apply");
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains(named);
    await Assert.That(message).Contains("OrderCollectives");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnICollectiveApplyForImplementationWithoutTheAttribute_IsAnErrorAsync() {
    const string source = COLLECTIVE_PREAMBLE + """
        public class OrderCollectives : ICollectiveApplyFor<Order, ArchiveOrders> {
          public ICollectiveSpec<Order> Apply(ArchiveOrders evt, ICollectiveQuery query) => null!;
        }
        """;

    var reported = await ReportedAsync(source, ID);

    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("[CollectiveApplyFor]")
      .Because("the interface is not how a collective handler is found; the attribute is");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  public async Task AnICollectiveApplyForImplementationWithTheAttribute_IsNotReportedAsync() {
    const string source = COLLECTIVE_PREAMBLE + """
        public class OrderCollectives : ICollectiveApplyFor<Order, ArchiveOrders> {
          [CollectiveApplyFor]
          public ICollectiveSpec<Order> Apply(ArchiveOrders evt, ICollectiveQuery query) => null!;
          public ICollectiveSpec<Order> Unrelated(ArchiveOrders evt, ICollectiveQuery query) => null!;
        }
        """;

    await Assert.That(await ReportedAsync(source, ID)).IsEmpty();
  }
}
