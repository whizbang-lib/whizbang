using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Coverage-focused tests for <see cref="PinnedIdAnalyzer"/> targeting branches the primary
/// test suite does not reach: a non-class/non-struct named type (which the abstract-only
/// existing interface test does not exercise, since Roslyn reports interfaces as
/// <c>IsAbstract == true</c> and short-circuits earlier), and a <c>[PinnedId]</c> attribute
/// whose constructor argument value is not a string.
/// </summary>
[Category("Analyzers")]
public class PinnedIdAnalyzerCoverageTests {
  // If the analyzer stopped skipping non-class/non-struct named types (enums, delegates), every
  // enum in a consuming codebase would get spuriously flagged for missing [PinnedId], drowning
  // the real signal (a message type genuinely missing one) in noise.
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_EnumType_NoDiagnosticAsync() {
    const string source = """
        using Whizbang.Core;
        namespace TestApp;
        public enum OrderStatus { Pending, Shipped }
        public record OrderPlacedEvent : IEvent;
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    // The enum itself must never be flagged; the real offender (the event) still is.
    var matches = diagnostics.Where(d => d.Id == "WHIZ110").ToList();
    await Assert.That(matches).Count().IsEqualTo(1);
    await Assert.That(matches[0].GetMessage(CultureInfo.InvariantCulture)).Contains("OrderPlacedEvent");
  }

  // A [PinnedId] argument that isn't a string (e.g. a null literal forced through the
  // null-forgiving operator) must not crash the analyzer. Today this is treated as "nothing to
  // validate" and silently returns rather than reporting WHIZ102 -- worth pinning down
  // explicitly, since a regression either direction (a crash, or suddenly flagging it) changes
  // observable behavior for a malformed attribute application.
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_PinnedIdWithNullConstructorArgument_NoDiagnosticAsync() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;
        namespace TestApp;
        [PinnedId(null!)]
        public record OrderPlacedEvent : IEvent;
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ110" or "WHIZ111" or "WHIZ112")).IsEmpty();
  }
}
