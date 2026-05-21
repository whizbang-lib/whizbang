using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for PinnedIdAnalyzer WHIZ110/111/112.
/// Validates enforcement of [PinnedId] on concrete IMessage and IPerspectiveFor&lt;&gt; types.
/// </summary>
[Category("Analyzers")]
public class PinnedIdAnalyzerTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ConcreteEventWithPinnedId_NoDiagnosticAsync() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;
        namespace TestApp;
        [PinnedId("11111111-2222-3333-4444-555555555555")]
        public record OrderPlacedEvent : IEvent;
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ110" or "WHIZ111" or "WHIZ112")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ConcreteEventWithoutPinnedId_ReportsWhiz100Async() {
    const string source = """
        using Whizbang.Core;
        namespace TestApp;
        public record OrderPlacedEvent : IEvent;
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    var matches = diagnostics.Where(d => d.Id == "WHIZ110").ToList();
    await Assert.That(matches).Count().IsEqualTo(1);
    await Assert.That(matches[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(matches[0].GetMessage(CultureInfo.InvariantCulture)).Contains("OrderPlacedEvent");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ConcreteCommandWithoutPinnedId_ReportsWhiz100Async() {
    const string source = """
        using Whizbang.Core;
        namespace TestApp;
        public record PlaceOrderCommand : ICommand;
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    var matches = diagnostics.Where(d => d.Id == "WHIZ110").ToList();
    await Assert.That(matches).Count().IsEqualTo(1);
    await Assert.That(matches[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_AbstractBaseEvent_NoDiagnosticAsync() {
    const string source = """
        using Whizbang.Core;
        namespace TestApp;
        public abstract record BaseEvent : IEvent;
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ110" or "WHIZ111")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InterfaceInheritingIEvent_NoDiagnosticAsync() {
    const string source = """
        using Whizbang.Core;
        namespace TestApp;
        public interface IOrderEvent : IEvent;
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ110" or "WHIZ111")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_PerspectiveWithoutPinnedId_ReportsWhiz101Async() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;
        namespace TestApp;
        public record OrderView;
        public record OrderPlacedEvent : IEvent;
        public class OrderPerspective : IPerspectiveFor<OrderView, OrderPlacedEvent> {
          public OrderView Apply(OrderView? current, OrderPlacedEvent @event) => current ?? new();
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    var perspectiveMatches = diagnostics.Where(d => d.Id == "WHIZ111").ToList();
    await Assert.That(perspectiveMatches).Count().IsEqualTo(1);
    await Assert.That(perspectiveMatches[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(perspectiveMatches[0].GetMessage(CultureInfo.InvariantCulture)).Contains("OrderPerspective");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_PerspectiveWithActionsForWithoutPinnedId_ReportsWhiz101Async() {
    // IPerspectiveWithActionsFor types must be subject to the same [PinnedId]
    // enforcement as IPerspectiveFor; otherwise the analyzer silently lets a
    // pinned-id-less perspective ship.
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;
        namespace TestApp;
        public record OrderView;
        public record OrderShippedEvent : IEvent;
        public class ActionsOrderPerspective : IPerspectiveWithActionsFor<OrderView, OrderShippedEvent> {
          public ApplyResult<OrderView> Apply(OrderView? current, OrderShippedEvent @event) =>
            ApplyResult<OrderView>.Update(current ?? new());
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    var perspectiveMatches = diagnostics.Where(d => d.Id == "WHIZ111").ToList();
    await Assert.That(perspectiveMatches).Count().IsEqualTo(1);
    await Assert.That(perspectiveMatches[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(perspectiveMatches[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ActionsOrderPerspective");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_PerspectiveWithPinnedId_NoDiagnosticAsync() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;
        using Whizbang.Core.Perspectives;
        namespace TestApp;
        public record OrderView;
        [PinnedId("33333333-4444-5555-6666-777777777777")]
        public record OrderPlacedEvent : IEvent;
        [PinnedId("88888888-9999-aaaa-bbbb-cccccccccccc")]
        public class OrderPerspective : IPerspectiveFor<OrderView, OrderPlacedEvent> {
          public OrderView Apply(OrderView? current, OrderPlacedEvent @event) => current ?? new();
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ110" or "WHIZ111" or "WHIZ112")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_PinnedIdValueNotAGuid_ReportsWhiz102Async() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;
        namespace TestApp;
        [PinnedId("definitely-not-a-guid")]
        public record OrderPlacedEvent : IEvent;
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    var matches = diagnostics.Where(d => d.Id == "WHIZ112").ToList();
    await Assert.That(matches).Count().IsEqualTo(1);
    await Assert.That(matches[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(matches[0].GetMessage(CultureInfo.InvariantCulture)).Contains("definitely-not-a-guid");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_PinnedIdValidGuid_NoDiagnosticAsync() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;
        namespace TestApp;
        [PinnedId("11111111-2222-3333-4444-555555555555")]
        public record OrderPlacedEvent : IEvent;
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ112")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NonMessageType_NoDiagnosticAsync() {
    const string source = """
        namespace TestApp;
        public class SomeService { public void DoSomething() { } }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ110" or "WHIZ111")).IsEmpty();
  }
}
