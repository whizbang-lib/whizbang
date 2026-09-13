using TUnit.Assertions.Extensions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Which fields are unaccounted for when a request can order by any of them.
/// </summary>
/// <remarks>
/// <para>
/// The exposure is what makes this checkable. A request-time sort writes no predicate in source, so
/// the ordinary advisory cannot see it and says nothing about the models that are queried hardest.
/// This answers the other question: given that any field could be ordered by, which ones has nobody
/// accounted for.
/// </para>
/// <para>
/// The negative cases matter as much as the positive one. Reporting a field that cannot carry an
/// index, or one already promoted, or the stream key, would be advice with nothing to do about it,
/// and a diagnostic that cannot be acted on gets suppressed wholesale and stops being read.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
public class SortableExposureDiscoveryTests {
  private static string _model(string body, string classAttributes = "") => $$"""
    using System;
    using System.Collections.Generic;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    {{classAttributes}}
    public class Model {
    {{body}}
    }
    """;

  private static string[] _unattributed(string source) {
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source);

    // An attribute that does not bind looks exactly like an attribute nobody wrote, so without this
    // every "not reported" case below would pass for the wrong reason and this file would be a
    // statement about the reference list.
    var unbound = compilation.GetDiagnostics()
      .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
      .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Where(m => m.Contains("Indexed", StringComparison.Ordinal)
        || m.Contains("StreamId", StringComparison.Ordinal)
        || m.Contains("SuppressIndexAdvisory", StringComparison.Ordinal)
        || m.Contains("IndexAllFields", StringComparison.Ordinal)
        || m.Contains("PhysicalField", StringComparison.Ordinal)
        || m.Contains("VectorField", StringComparison.Ordinal))
      .ToList();
    if (unbound.Count > 0) {
      throw new InvalidOperationException(
        "the framework attributes did not bind, so this test would measure the harness: "
        + string.Join("; ", unbound.Take(3)));
    }

    var model = compilation.GetTypeByMetadataName("TestApp.Model");
    return [.. global::Whizbang.Generators.Shared.Models.SortableExposureDiscovery
      .UnattributedFields(model)];
  }

  /// <summary>An ordinary field with no declaration is what this exists to surface.</summary>
  [Test]
  public async Task AFieldWithNoDeclarationIsReportedAsync() {
    var fields = _unattributed(_model("""
        [StreamId]
        public Guid Id { get; init; }

        public string JobName { get; init; } = string.Empty;
        public int Version { get; init; }
      """));

    await Assert.That(fields).Contains("JobName")
      .Because("a request can order by it and nothing has said how that is served");
    await Assert.That(fields).Contains("Version");
  }

  /// <summary>The stream key is the row's primary key in either storage form.</summary>
  [Test]
  public async Task TheStreamKeyIsNotReportedAsync() {
    var fields = _unattributed(_model("""
        [StreamId]
        public Guid Id { get; init; }
      """));

    await Assert.That(fields).IsEmpty();
  }

  /// <summary>A field that already carries a declaration has its answer.</summary>
  [Test]
  public async Task AnIndexedFieldIsNotReportedAsync() {
    var fields = _unattributed(_model("""
        [StreamId]
        public Guid Id { get; init; }

        [Indexed]
        public string Status { get; init; } = string.Empty;
      """));

    await Assert.That(fields).IsEmpty();
  }

  /// <summary>A recorded decision counts as an answer, which is the point of recording it.</summary>
  [Test]
  public async Task ASuppressedFieldIsNotReportedAsync() {
    var fields = _unattributed(_model("""
        [StreamId]
        public Guid Id { get; init; }

        [SuppressIndexAdvisory("this list is never long enough to matter")]
        public string Note { get; init; } = string.Empty;
      """));

    await Assert.That(fields).IsEmpty();
  }

  /// <summary>A promoted field carries its own column and its own index question.</summary>
  [Test]
  [Arguments("[PhysicalField]")]
  [Arguments("[VectorField(8)]")]
  public async Task APromotedFieldIsNotReportedAsync(string promotion) {
    var fields = _unattributed(_model($$"""
        [StreamId]
        public Guid Id { get; init; }

        {{promotion}}
        public float[] Value { get; init; } = Array.Empty<float>();
      """));

    await Assert.That(fields).IsEmpty();
  }

  /// <summary>
  /// A field whose stored form cannot carry an index is not reported, because indexing is not on offer.
  /// </summary>
  /// <remarks>
  /// WHIZ303 is the diagnostic for asking anyway. Naming such a field here would tell an author to
  /// do something the framework would then refuse.
  /// </remarks>
  [Test]
  public async Task AFieldThatCannotCarryAnIndexIsNotReportedAsync() {
    var fields = _unattributed(_model("""
        [StreamId]
        public Guid Id { get; init; }

        public List<string> Tags { get; init; } = new();
        public object Payload { get; init; } = new();
      """));

    await Assert.That(fields).IsEmpty()
      .Because("no index could be built over either, so there is no choice to offer");
  }

  /// <summary>A model asking for every field has answered for all of them at once.</summary>
  [Test]
  public async Task AModelIndexingEveryFieldIsNotReportedAsync() {
    var fields = _unattributed(_model("""
        [StreamId]
        public Guid Id { get; init; }

        public string JobName { get; init; } = string.Empty;
      """, "[IndexAllFields]"));

    await Assert.That(fields).IsEmpty();
  }

  /// <summary>A decision recorded on the model covers its fields wholesale.</summary>
  [Test]
  public async Task AModelWithARecordedDecisionIsNotReportedAsync() {
    var fields = _unattributed(_model("""
        [StreamId]
        public Guid Id { get; init; }

        public string JobName { get; init; } = string.Empty;
      """, "[SuppressIndexAdvisory(\"admin screen, tens of rows\")]"));

    await Assert.That(fields).IsEmpty();
  }

  /// <summary>
  /// A document stored as one opaque value has no per-field extraction to index.
  /// </summary>
  /// <remarks>
  /// Naming its fields would be advice that cannot be taken: there is nothing to build an index
  /// over. A declared index on such a model is WHIZ304's business, not this one's.
  /// </remarks>
  [Test]
  public async Task AnOpaquelyStoredModelIsNotReportedAsync() {
    var fields = _unattributed("""
      using System;
      using System.Collections.Generic;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record Turn(Guid TurnId, IReadOnlyList<string>? Tags = null);

      public abstract class Payment { }

      public class Model {
        [StreamId]
        public Guid Id { get; init; }

        public string JobName { get; init; } = string.Empty;

        public Payment Method { get; init; } = null!;
      }
      """);

    await Assert.That(fields).IsEmpty()
      .Because("nothing inside an opaque document is a mapped property, so no extraction exists to "
        + "index and the advice would be impossible to follow");
  }
}
