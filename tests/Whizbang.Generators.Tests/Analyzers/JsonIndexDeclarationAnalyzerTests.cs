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
        [JsonIndexed]
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
        [JsonIndexed]
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
    var source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public enum Grade { Low, High }

      public record OrderModel {
        [StreamId]
        public Guid OrderId { get; init; }

        [JsonIndexed]
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
    var source = """
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
        [JsonIndexed]
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
        [JsonIndexed(JsonIndexKind.None)]
        public object Value { get; init; } = new();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz303(diagnostics)).IsEmpty()
      .Because("the author has said this field carries no index, which is the thing the diagnostic "
        + "would otherwise be asking them to say");
  }
}
