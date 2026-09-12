using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions.Extensions;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// An index declared on a model whose document cannot be queried through an extraction is reported
/// at build time.
/// </summary>
/// <remarks>
/// <para>
/// A model holding an abstract member cannot be mapped property by property, because that mapping
/// reconstructs the declared type and loses the derived one it was given. Such a model is stored as
/// a single serialized value instead. Nothing inside that value is a mapped property, so a filter on
/// a field within it never compiles to the extraction an index would be built over.
/// </para>
/// <para>
/// The index would still be created, maintained on every write, and never scanned: cost with no
/// benefit, and a developer believing a field is indexed while every query on it reads the whole
/// table. That is the more expensive of the two mistakes precisely because nothing surfaces it,
/// which is why it is said out loud here rather than skipped in silence.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public class JsonIndexStorageAnalyzerTests {
  private static IEnumerable<Diagnostic> _whiz304(IEnumerable<Diagnostic> diagnostics) =>
    diagnostics.Where(d => d.Id == "WHIZ304");

  private const string POLYMORPHIC_MODEL = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public abstract class PaymentMethod {
      public string Name { get; init; } = "";
    }

    public record OrderModel {
      [StreamId]
      public Guid OrderId { get; init; }

      public PaymentMethod? Payment { get; init; }

      [JsonIndexed]
      public string Reference { get; init; } = "";
    }
    """;

  /// <summary>
  /// A field index declared on a model that has to be stored opaquely is reported.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnIndexOnAnOpaquelyStoredModelIsReportedAsync() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(
      POLYMORPHIC_MODEL);

    await Assert.That(_whiz304(diagnostics)).IsNotEmpty()
      .Because("a filter on a field of an opaquely stored document never compiles to an extraction, "
        + "so the index is maintained on every write and scanned by nothing");
  }

  /// <summary>
  /// A blanket declaration on such a model is reported too, unlike the per-field case in WHIZ303.
  /// </summary>
  /// <remarks>
  /// The reason the two differ is the reason WHIZ303 stays quiet about a blanket declaration: there,
  /// the blanket claims nothing about the one field it cannot cover, and the rest of the model is
  /// still indexed. Here nothing on the model can be indexed at all, so the blanket is a claim that
  /// fails completely rather than one that mostly holds.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ABlanketDeclarationOnAnOpaquelyStoredModelIsReportedAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public abstract class PaymentMethod {
        public string Name { get; init; } = "";
      }

      [IndexAllFields]
      public record OrderModel {
        [StreamId]
        public Guid OrderId { get; init; }

        public PaymentMethod? Payment { get; init; }

        public string Reference { get; init; } = "";
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz304(diagnostics)).IsNotEmpty()
      .Because("asking for every field to be indexed on a model where no field can be is a claim "
        + "that fails entirely, not one that mostly holds");
  }

  /// <summary>
  /// A model that declares no index is left alone, because it claimed nothing.
  /// </summary>
  /// <remarks>
  /// Opaque storage is a legitimate choice, not a defect. Reporting every model that made it would
  /// be telling authors off for modeling a hierarchy.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnOpaquelyStoredModelThatDeclaresNoIndexIsNotReportedAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public abstract class PaymentMethod {
        public string Name { get; init; } = "";
      }

      public record OrderModel {
        [StreamId]
        public Guid OrderId { get; init; }

        public PaymentMethod? Payment { get; init; }

        public string Reference { get; init; } = "";
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz304(diagnostics)).IsEmpty()
      .Because("storing a hierarchy opaquely is a modeling decision rather than a mistake, and only "
        + "an index declared over it is a claim that cannot be met");
  }

  /// <summary>
  /// An ordinary record is not reported, which is the case that would have drowned out the rest.
  /// </summary>
  /// <remarks>
  /// A record used to be classified as opaquely stored on every occasion, because the compiler
  /// generates a protected <c>EqualityContract</c> of type <c>System.Type</c> and that type is an
  /// abstract class. Had this diagnostic shipped against that behavior it would have fired on nearly
  /// every model in existence and reported a detection bug as if it were the author's modeling. The
  /// detection now considers only public properties; this test is what keeps that true.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnOrdinaryRecordIsNotReportedAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record OrderModel {
        [StreamId]
        public Guid OrderId { get; init; }

        [JsonIndexed]
        public string Reference { get; init; } = "";

        [JsonIndexed]
        public int Quantity { get; init; }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(_whiz304(diagnostics)).IsEmpty()
      .Because("a record of a string and an int is mapped property by property like any other "
        + "model, and its indexes are reachable");
  }

  /// <summary>
  /// The message names the model and the alternative, since a promoted column works whatever the
  /// document's storage.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task TheMessageNamesTheModelAndTheAlternativeAsync() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(
      POLYMORPHIC_MODEL);
    var message = _whiz304(diagnostics).First()
      .GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    await Assert.That(message).Contains("OrderModel", StringComparison.Ordinal);
    await Assert.That(message).Contains("PhysicalField", StringComparison.Ordinal)
      .Because("a promoted column is a real column with a real index, which is reachable no matter "
        + "how the rest of the document is stored");
  }
}
