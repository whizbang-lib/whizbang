using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions.Extensions;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// An index declared on a field whose extraction cannot carry one is reported at build time.
/// </summary>
/// <remarks>
/// <para>
/// A declaration on a specific property is a claim about that property, so being unable to honor it
/// is worth saying out loud. The alternative failure modes are both bad: emitting the index anyway
/// fails the schema pass at startup with a PostgreSQL error naming no property, and skipping it
/// silently leaves a developer believing a field is indexed while every query on it scans.
/// </para>
/// <para>
/// A blanket declaration on the model is deliberately not reported. It is not a claim about any
/// particular field, so naming each field it skips would be noise rather than information.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public class JsonIndexDeclarationAnalyzerTests {
  private static string _model(string properties) => $$"""
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public record OrderModel {
      [StreamId]
      public Guid OrderId { get; init; }

    {{properties}}
    }
    """;

  private static IEnumerable<Diagnostic> _whiz303(IEnumerable<Diagnostic> diagnostics) =>
    diagnostics.Where(d => d.Id == "WHIZ303");

  private static IEnumerable<Diagnostic> _whiz305(IEnumerable<Diagnostic> diagnostics) =>
    diagnostics.Where(d => d.Id == "WHIZ305");

  /// <summary>
  /// A type the framework does not store in a form any immutable cast can reach is reported.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The date family used to be the whole of this list. A date was stored as a rendering, and the
  /// cast from text to a timestamp is STABLE rather than IMMUTABLE, so PostgreSQL refused to index
  /// it. That is no longer true: dates, times and durations are stored as numbers, and a numeric
  /// cast is immutable. They moved to the list below.
  /// </para>
  /// <para>
  /// What is left is genuinely unreachable rather than awkwardly stored: a nested object or a
  /// collection has no single scalar to extract, so there is nothing an index could be built over.
  /// </para>
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("char")]
  [Arguments("object")]
  public async Task ATypeThatCannotCarryAnIndex_IsReportedAsync(string type) {
    var source = _model($$"""
        [Indexed]
        public {{type}} Value { get; init; }
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz303(diagnostics)).IsNotEmpty()
      .Because($"a declared index on a {type} cannot be built, and silence would leave the author "
        + "believing the field is indexed while every query on it scans");
  }

  /// <summary>Every type that can carry one is left alone.</summary>
  /// <remarks>
  /// The date family is here because its stored form changed, not because the index rules did.
  /// PostgreSQL still refuses a stable expression; a number simply is not one. If any of these ever
  /// starts being reported again, the stored form regressed rather than the analyzer.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("string")]
  [Arguments("int")]
  [Arguments("long")]
  [Arguments("short")]
  [Arguments("decimal")]
  [Arguments("double")]
  [Arguments("float")]
  [Arguments("bool")]
  [Arguments("Guid")]
  [Arguments("int?")]
  [Arguments("DateTime")]
  [Arguments("DateTimeOffset")]
  [Arguments("DateOnly")]
  [Arguments("TimeOnly")]
  [Arguments("TimeSpan")]
  [Arguments("DateTime?")]
  public async Task AnIndexableType_IsNotReportedAsync(string type) {
    var source = _model($$"""
        [Indexed]
        public {{type}} Value { get; init; }
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz303(diagnostics)).IsEmpty();
  }

  /// <summary>
  /// An enumeration is carried by whichever integer it is stored as, so it is indexable.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnEnumerationIsNotReportedAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public enum Grade { Low, High }

      public record OrderModel {
        [StreamId]
        public Guid OrderId { get; init; }

        [Indexed]
        public Grade Level { get; init; }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz303(diagnostics)).IsEmpty();
  }

  /// <summary>
  /// A blanket declaration is not reported for the fields it cannot cover, because it never claimed
  /// them individually.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ABlanketDeclarationIsNotReportedAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      [IndexAllFields]
      public record OrderModel {
        [StreamId]
        public Guid OrderId { get; init; }

        public object Unreachable { get; init; } = new();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz303(diagnostics)).IsEmpty()
      .Because("a declaration over every field is not a claim about any one of them, so naming each "
        + "skip would be noise");
  }

  /// <summary>
  /// The message names the property and says what to do instead.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task TheMessageNamesThePropertyAndTheAlternativeAsync() {
    var source = _model("""
        [Indexed]
        public object OccurredAt { get; init; } = new();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);
    var message = _whiz303(diagnostics).Single().GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    await Assert.That(message).Contains("OccurredAt", StringComparison.Ordinal);
    await Assert.That(message).Contains("PhysicalField", StringComparison.Ordinal)
      .Because("a date that has to be range-filtered wants a real column, which is the answer "
        + "available today");
  }

  /// <summary>
  /// Opting out is not a claim, so it is not reported even on a type that could not carry one.
  /// </summary>
  /// <remarks>
  /// The diagnostic exists because a declaration on a specific field is a claim about that field.
  /// Asking for no kind is the opposite of a claim: the author has said this field is not indexed,
  /// which is exactly what the diagnostic would otherwise be telling them.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task OptingOutIsNotReportedAsync() {
    var source = _model("""
        [Indexed(IndexKinds.None)]
        public object Value { get; init; } = new();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz303(diagnostics)).IsEmpty()
      .Because("the author has said this field carries no index, which is the thing the diagnostic "
        + "would otherwise be asking them to say");
  }

  /// <summary>
  /// A promoted field is judged on its column, not on what the document's cast could carry.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the consequence of <c>[Indexed]</c> being universal: the same attribute asks for an
  /// index on either side of the promotion, so the diagnostic has to know which side it is on before
  /// deciding whether the cast out of the document is immutable. For a promoted field that question
  /// is simply irrelevant, because the index is on a real column.
  /// </para>
  /// <para>
  /// A vector is the case that makes it obvious and the one that caught it: no cast out of a document
  /// reaches an array, so every indexed vector field was reported as impossible while its column
  /// index was being created perfectly well.
  /// </para>
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("[PhysicalField]")]
  [Arguments("[VectorField(8)]")]
  public async Task APromotedFieldIsNotJudgedOnTheDocumentCastAsync(string promotion) {
    var source = $$"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record OrderModel {
        [StreamId]
        public Guid OrderId { get; init; }

        {{promotion}}
        [Indexed]
        public float[] Value { get; init; } = Array.Empty<float>();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz303(diagnostics)).IsEmpty()
      .Because("the index goes on the column, so whether a cast out of the document could carry one "
        + "says nothing about whether this index can be built");
  }

  /// <summary>
  /// Asked about nothing, the shared question answers nothing.
  /// </summary>
  /// <remarks>
  /// The guard exists because three callers ask this and one of them resolves a symbol that can be
  /// absent. Covered directly because a defensive branch nothing reaches is indistinguishable from a
  /// defensive branch that is wrong.
  /// </remarks>
  [Test]
  public async Task NoPropertyDeclaresNothingAsync() {
    await Assert.That(global::Whizbang.Generators.Shared.Models.JsonIndexDiscovery.DeclaredKind(null))
      .IsNull()
      .Because("null is silence rather than an opt-out, so a caller with no symbol must not be told "
        + "the field declined an index");
  }

  /// <summary>
  /// A capability that only applies to text, asked for on something else, is reported and named.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Both of these are properties of text. Substring matching needs an index built for pattern
  /// matching rather than ordering, and case folding changes the expression the index is built over;
  /// neither means anything for a number or a date. The discovery drops them for such a field, which
  /// is the right thing to build and the wrong thing to do in silence.
  /// </para>
  /// <para>
  /// Worth a diagnostic because the declaration reads as a claim either way, and because a field
  /// asking only for substring matching used to get no index at all: the author asked for one thing
  /// and the answer was nothing, with the attribute still sitting there.
  /// </para>
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("[Indexed(IndexKinds.Substring)]", "int Count", "substring matching")]
  [Arguments("[Indexed(caseInsensitive: true)]", "int Count", "case folding")]
  [Arguments("[Indexed(IndexKinds.Substring, caseInsensitive: true)]", "int Count",
    "substring matching and case folding")]
  [Arguments("[Indexed(IndexKinds.Substring)]", "DateTime OccurredAt", "substring matching")]
  [Arguments("[Indexed(caseInsensitive: true)]", "Guid Reference", "case folding")]
  public async Task ACapabilityThatOnlyAppliesToText_IsReportedAsync(
    string declaration, string property, string expected) {
    var source = _model($$"""
        {{declaration}}
        public {{property}} { get; init; }
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);
    var reported = _whiz305(diagnostics).ToList();

    await Assert.That(reported).HasSingleItem()
      .Because("one field asked for one thing it cannot have, so it gets one message");
    await Assert.That(reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains(expected, StringComparison.Ordinal)
      .Because("the fix is a one-word edit, so the message has to name which word");
  }

  /// <summary>
  /// The same capabilities on a text field are silent, because text is exactly what they apply to.
  /// </summary>
  /// <remarks>
  /// The other half of this pair lives in
  /// <c>JsonIndexGenerationTests.ATrigramIndexUsesGinWithTheTrigramOperatorClassAsync</c> and
  /// <c>AFieldComparedBothWaysGetsAnIndexForEachAsync</c>, which assert the statements are actually
  /// emitted for text. Silence here and emission there are what pin the behavior between them: on
  /// its own, silence would also be satisfied by a discovery that had quietly stopped building
  /// either index.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("[Indexed(IndexKinds.Substring)]", "string Label")]
  [Arguments("[Indexed(caseInsensitive: true)]", "string Label")]
  [Arguments("[Indexed(IndexKinds.Substring, caseInsensitive: true)]", "string Label")]
  [Arguments("[Indexed]", "int Count")]
  [Arguments("[Indexed(IndexKinds.Ordered)]", "DateTime OccurredAt")]
  public async Task ACapabilityThatApplies_IsNotReportedAsync(string declaration, string property) {
    var source = _model($$"""
        {{declaration}}
        public {{property}} { get; init; }
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz305(diagnostics)).IsEmpty()
      .Because("the capability is buildable for this field, so there is nothing to report and a "
        + "message here would be noise on correct code");
  }

  /// <summary>
  /// A field whose type can carry no index at all is reported once, for that, rather than twice.
  /// </summary>
  /// <remarks>
  /// The two findings have different fixes. WHIZ303 says to promote the field or drop the
  /// declaration; WHIZ305 says to change one word. Reporting both would offer a choice between them
  /// where only the first is available, so the larger problem is the one that speaks.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AFieldThatCanCarryNoIndexIsNotAlsoToldAboutTheCapabilityAsync() {
    var source = _model("""
        [Indexed(IndexKinds.Substring, caseInsensitive: true)]
        public char Initial { get; init; }
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz303(diagnostics)).HasSingleItem()
      .Because("no immutable cast reaches this field, which is the finding that decides what to do");
    await Assert.That(_whiz305(diagnostics)).IsEmpty()
      .Because("naming a capability to change would suggest an edit that leaves the field still "
        + "unindexable");
  }
}
