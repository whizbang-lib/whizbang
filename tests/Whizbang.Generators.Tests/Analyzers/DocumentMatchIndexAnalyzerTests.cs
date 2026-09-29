using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// WHIZ307 and WHIZ308: a filter that compiles to a whole-document match, checked against what the
/// model declares its queries do.
/// </summary>
/// <remarks>
/// <para>
/// An equality filter on a field with no index of its own is compiled into a whole-document match,
/// and only the index over the whole document answers it. A model that declares its queries never
/// match that way does not get the index, so such a filter against it reads every row. That is the
/// silent sequential scan a build-time warning exists to prevent (WHIZ307).
/// </para>
/// <para>
/// A model that declares nothing still gets the index for now. Each filter relying on it is noted
/// (WHIZ308) so the list of queries that would lose it is visible before anyone opts out.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz307</docs>
[Category("Analyzers")]
public class DocumentMatchIndexAnalyzerTests {
  private static string _source(string modelAttribute, string predicate) => $$"""
      using System;
      using System.Collections.Generic;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      {{modelAttribute}}
      public class ShipmentModel {
        [StreamId]
        public Guid ShipmentId { get; init; }

        public string Carrier { get; init; } = string.Empty;

        [Indexed]
        public string Status { get; init; } = string.Empty;

        [PhysicalField]
        [Indexed]
        public Guid Owner { get; init; }

        public int Weight { get; init; }

        public bool Fragile { get; init; }

        public Guid? Batch { get; init; }
      }

      public class ShipmentRepository {
        private readonly IQueryable<PerspectiveRow<ShipmentModel>> _rows = null!;

        public object Find(Guid id) =>
          _rows.Where(r => {{predicate}}).ToList();
      }
      """;

  private static async Task<List<Diagnostic>> _diagnosticsAsync(string modelAttribute, string predicate, string id) =>
    [.. (await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(_source(modelAttribute, predicate)))
      .Where(d => d.Id == id)];

  // ========================================
  // WHIZ307: the index the filter needs is not built
  // ========================================

  /// <summary>
  /// A whole-document match against a model that opted out has no index to use, and says so.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task WholeDocumentMatch_OnAModelThatOptedOut_WarnsAsync() {
    var reported = await _diagnosticsAsync("[PerspectiveQueries(MatchOnAnyField = false)]", "r.Data.Carrier == \"x\"", "WHIZ307");

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("ShipmentModel.Carrier");
    await Assert.That(message).Contains("MatchOnAnyField");
  }

  /// <summary>A metadata match warns unless the model asked for metadata matching.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("")]
  [Arguments("[PerspectiveQueries(MatchOnAnyField = true)]")]
  public async Task MetadataMatch_WithoutTheOptIn_WarnsAsync(string modelAttribute) {
    var reported = await _diagnosticsAsync(modelAttribute, "r.Metadata.EventType == \"Shipped\"", "WHIZ307");

    await Assert.That(reported).Count().IsEqualTo(1);
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("Metadata.EventType");
    await Assert.That(message).Contains("MatchOnMetadata");
  }

  // ========================================
  // WHIZ307 stays quiet
  // ========================================

  /// <summary>
  /// Every shape that does not compile to a whole-document match, or that has an index either way,
  /// is left alone.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("[PerspectiveQueries(MatchOnAnyField = true)]", "r.Data.Carrier == \"x\"")]
  [Arguments("[PerspectiveQueries(MatchOnAnyField = false)]", "r.Data.Status == \"x\"")]
  [Arguments("[PerspectiveQueries(MatchOnAnyField = false)]", "r.Data.Owner == id")]
  [Arguments("[PerspectiveQueries(MatchOnAnyField = false)]", "r.Data.ShipmentId == id")]
  [Arguments("[PerspectiveQueries(MatchOnAnyField = false)]", "r.Data.Weight > 3")]
  [Arguments("[PerspectiveQueries(MatchOnAnyField = false)]", "r.Data.Carrier != \"x\"")]
  [Arguments("[PerspectiveQueries(MatchOnAnyField = false)]", "r.Data.Carrier == null")]
  [Arguments("[PerspectiveQueries(MatchOnMetadata = true)]", "r.Metadata.EventType == \"Shipped\"")]
  [Arguments("", "r.Metadata.Timestamp > DateTime.UnixEpoch")]
  [Arguments("", "r.Id == id")]
  public async Task NoWholeDocumentMatchWithoutAnIndex_DoesNotWarnAsync(string modelAttribute, string predicate) {
    await Assert.That(await _diagnosticsAsync(modelAttribute, predicate, "WHIZ307")).IsEmpty();
  }

  /// <summary>
  /// A reasoned opt-out on the model is honored, the same one that stands down WHIZ302, because a
  /// scan somebody chose is a decision rather than a defect.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("r.Data.Carrier == \"x\"")]
  [Arguments("r.Metadata.EventType == \"Shipped\"")]
  public async Task AReasonedSuppressionOnTheModel_StandsItDownAsync(string predicate) {
    var reported = await _diagnosticsAsync(
      "[PerspectiveQueries(MatchOnAnyField = false)]\n[SuppressIndexAdvisory(\"a handful of rows per tenant\")]",
      predicate,
      "WHIZ307");

    await Assert.That(reported).IsEmpty();
  }

  /// <summary>
  /// A model stored as one serialized value has no mapped path into its fields, so there is no
  /// whole-document match to answer, and WHIZ302 already says what does work there.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnOpaquelyStoredModel_IsNotReportedAsync() {
    const string source = """
      using System;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public abstract class PaymentMethod {
        public string Name { get; init; } = string.Empty;
      }

      [PerspectiveQueries(MatchOnAnyField = false)]
      public class InvoiceModel {
        [StreamId]
        public Guid InvoiceId { get; init; }

        public PaymentMethod? Payment { get; init; }

        public string Reference { get; init; } = string.Empty;
      }

      public class InvoiceRepository {
        private readonly IQueryable<PerspectiveRow<InvoiceModel>> _rows = null!;

        public object Find() => _rows.Where(r => r.Data.Reference == "x").ToList();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ307" or "WHIZ308")).IsEmpty();
  }

  /// <summary>A metadata member read outside a filter is a projection and costs no index.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AMetadataProjection_DoesNotWarnAsync() {
    var source = _source("", "true").Replace(
      "_rows.Where(r => true).ToList();",
      "_rows.Select(r => r.Metadata.EventType == \"Shipped\").ToList();",
      StringComparison.Ordinal);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ307")).IsEmpty();
  }

  /// <summary>A metadata-shaped member on something that is not a perspective row is not a perspective query.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AMetadataMemberOffAnotherType_DoesNotWarnAsync() {
    const string source = """
      using System.Linq;

      namespace TestApp;

      public class Info { public string EventType { get; init; } = ""; }
      public class Holder { public Info Metadata { get; init; } = new(); }

      public class Other {
        private readonly IQueryable<Holder> _rows = null!;
        public object Find() => _rows.Where(h => h.Metadata.EventType == "x").ToList();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ307")).IsEmpty();
  }

  // ========================================
  // WHIZ308: relying on the transitional default
  // ========================================

  /// <summary>
  /// A whole-document match on a model that declares nothing is noted, as information, because the
  /// index it relies on is kept by a default rather than a decision.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task WholeDocumentMatch_OnAnUndeclaredModel_IsNotedAsync() {
    var reported = await _diagnosticsAsync("", "r.Data.Carrier == \"x\"", "WHIZ308");

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Info);
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ShipmentModel.Carrier");
  }

  /// <summary>
  /// A declaration either way ends the note: true is the decision to keep the index, and false is
  /// answered by WHIZ307 instead.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("[PerspectiveQueries(MatchOnAnyField = true)]")]
  [Arguments("[PerspectiveQueries(MatchOnAnyField = false)]")]
  public async Task WholeDocumentMatch_OnADeclaredModel_IsNotNotedAsync(string modelAttribute) {
    await Assert.That(await _diagnosticsAsync(modelAttribute, "r.Data.Carrier == \"x\"", "WHIZ308")).IsEmpty();
  }

  /// <summary>A filter a declared field index answers relies on nothing else.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AFilterOnADeclaredIndex_IsNotNotedAsync() {
    await Assert.That(await _diagnosticsAsync("", "r.Data.Status == \"x\"", "WHIZ308")).IsEmpty();
  }

  // ========================================
  // Set membership: values.Contains(field)
  // ========================================

  /// <summary>
  /// A set filter the translation compiles to a whole-document match is checked like equality,
  /// which is also the shape a request-composed "in" filter takes.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("new[] { \"a\" }.Contains(r.Data.Carrier)")]
  [Arguments("new List<string> { \"a\" }.Contains(r.Data.Carrier)")]
  [Arguments("Enumerable.Contains(new[] { \"a\" }, r.Data.Carrier)")]
  [Arguments("new[] { 1, 2 }.Contains(r.Data.Weight)")]
  public async Task ASetFilter_OnAModelThatOptedOut_WarnsAsync(string predicate) {
    var reported = await _diagnosticsAsync("[PerspectiveQueries(MatchOnAnyField = false)]", predicate, "WHIZ307");

    await Assert.That(reported).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Every set shape the translation leaves as an extraction, or that a declared index answers,
  /// is not a whole-document match.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("new List<string> { \"a\" }.Contains(r.Data.Status)")]
  [Arguments("!new[] { \"a\" }.Contains(r.Data.Carrier)")]
  [Arguments("new[] { true }.Contains(r.Data.Fragile)")]
  [Arguments("new Guid?[] { id }.Contains(r.Data.Batch)")]
  [Arguments("new HashSet<string> { \"a\" }.Contains(r.Data.Carrier)")]
  [Arguments("new object[] { \"a\" }.Contains(r.Data.Carrier)")]
  [Arguments("new List<object> { \"a\" }.Contains(r.Data.Carrier)")]
  [Arguments("\"abc\".Contains(r.Data.Carrier)")]
  [Arguments("new[] { \"a\" }.Contains(r.Data.Carrier, StringComparer.Ordinal)")]
  [Arguments("Enumerable.Contains(new[] { \"a\" }, r.Data.Carrier, StringComparer.Ordinal)")]
  [Arguments("Math.Abs(r.Data.Weight) > 3")]
  public async Task ASetShapeThatIsNotAWholeDocumentMatch_DoesNotWarnAsync(string predicate) {
    await Assert.That(await _diagnosticsAsync("[PerspectiveQueries(MatchOnAnyField = false)]", predicate, "WHIZ307")).IsEmpty();
  }

  /// <summary>A set filter on an undeclared model relies on the default index and is noted.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ASetFilter_OnAnUndeclaredModel_IsNotedAsync() {
    await Assert.That(await _diagnosticsAsync("", "new[] { \"a\" }.Contains(r.Data.Carrier)", "WHIZ308")).Count().IsEqualTo(1);
  }
}

