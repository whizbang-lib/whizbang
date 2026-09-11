using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for PerspectiveFilterIndexAnalyzer WHIZ302.
/// A perspective stores its model as JSON, so a filter on a property that was never promoted to a
/// physical column can only be answered by reading every row. These tests pin which shapes the
/// analyzer calls out, which ones it leaves alone, and how a team opts out of the advisory.
/// </summary>
/// <docs>operations/diagnostics/whiz302</docs>
[Category("Analyzers")]
public class PerspectiveFilterIndexAnalyzerTests {
  private const string PRELUDE = """
      using System;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public class ThingModel {
        [StreamId]
        public Guid ThingId { get; init; }

        [PhysicalField(Indexed = true)]
        public Guid IndexedOwnerId { get; init; }

        [PhysicalField(Unique = true)]
        public string UniqueCode { get; init; } = string.Empty;

        [PhysicalField]
        public string PromotedButNotIndexed { get; init; } = string.Empty;

        [VectorField(8)]
        public float[] Embedding { get; init; } = Array.Empty<float>();

        public string JsonOnly { get; init; } = string.Empty;

        public Guid AlsoJsonOnly { get; init; }

        public int Num { get; init; }

        public bool Flag { get; init; }

        public decimal Money { get; init; }

        public long Big { get; init; }

        public short Small { get; init; }

        public double Dbl { get; init; }

        public DateTime When { get; init; }

        public float Single { get; init; }

        public Mood State { get; init; }

        public DateTimeOffset WhenOffset { get; init; }
      }

      public enum Mood { Low, High }

      """;

  private static string _repositoryOver(string body) =>
    PRELUDE + $$"""
      public class ThingRepository {
        private readonly IQueryable<PerspectiveRow<ThingModel>> _rows = null!;

        public object Find(Guid id) {
      {{body}}
        }
      }
      """;

  private static IEnumerable<Diagnostic> _whiz302(IEnumerable<Diagnostic> diagnostics) =>
    diagnostics.Where(d => d.Id == "WHIZ302");

  // ========================================
  // The advisory fires
  // ========================================

  /// <summary>A filter on a JSON-only property is the case the advisory exists for.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnJsonOnlyField_ReportsAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.JsonOnly.Contains("ab")).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    var reported = _whiz302(diagnostics).ToList();
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ThingModel");
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("JsonOnly");
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  /// <summary>
  /// A property promoted to a physical column but left unindexed still scans, just a narrower
  /// column, so it is still worth saying.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnPromotedButUnindexedField_ReportsAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.PromotedButNotIndexed.Contains("ab")).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics).Select(d => d.GetMessage(CultureInfo.InvariantCulture)).Single())
      .Contains("PromotedButNotIndexed");
  }

  /// <summary>Each unindexed field in a compound predicate earns its own advisory.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnTwoJsonOnlyFields_ReportsEachAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.JsonOnly.Contains("ab") && r.Data.AlsoJsonOnly != id).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).Count().IsEqualTo(2);
  }

  /// <summary>Ordering wants an index for the same reason filtering does.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task OrderBy_OnJsonOnlyField_ReportsAsync() {
    var source = _repositoryOver("""
            return _rows.OrderBy(r => r.Data.JsonOnly).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).Count().IsEqualTo(1);
  }

  /// <summary>The Entity Framework async operators carry predicates too.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AsyncOperator_OnJsonOnlyField_ReportsAsync() {
    var source = PRELUDE + """
      public static class FakeAsyncExtensions {
        public static System.Threading.Tasks.Task<TSource?> FirstOrDefaultAsync<TSource>(
          this IQueryable<TSource> source,
          System.Linq.Expressions.Expression<Func<TSource, bool>> predicate) =>
            System.Threading.Tasks.Task.FromResult<TSource?>(default);
      }

      public class ThingRepository {
        private readonly IQueryable<PerspectiveRow<ThingModel>> _rows = null!;

        public object Find(Guid id) =>
          _rows.FirstOrDefaultAsync(r => r.Data.JsonOnly.Contains("ab"));
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).Count().IsEqualTo(1);
  }

  /// <summary>Query syntax is the same filter wearing different clothes.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task QuerySyntaxWhere_OnJsonOnlyField_ReportsAsync() {
    var source = _repositoryOver("""
            return (from r in _rows where r.Data.JsonOnly.Contains("ab") select r).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).Count().IsEqualTo(1);
  }

  // ========================================
  // The advisory stays quiet
  // ========================================

  /// <summary>An indexed physical column is the shape the advisory is asking for.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnIndexedPhysicalField_NoDiagnosticAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.IndexedOwnerId == id).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>A unique constraint carries its own index.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnUniquePhysicalField_NoDiagnosticAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.UniqueCode == "x").ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>The stream id is the row key, so it is indexed by construction.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnStreamIdField_NoDiagnosticAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.ThingId == id).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>A vector field is indexed unless its declaration turns the index off.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnVectorField_NoDiagnosticAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.Embedding.Length > 0).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>A vector field with its index switched off is back to being a scan.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnUnindexedVectorField_ReportsAsync() {
    var source = """
      using System;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public class RawModel {
        [VectorField(8, Indexed = false)]
        public float[] Embedding { get; init; } = Array.Empty<float>();
      }

      public class RawRepository {
        private readonly IQueryable<PerspectiveRow<RawModel>> _rows = null!;

        public object Find() => _rows.Where(r => r.Data.Embedding.Length > 0).ToList();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).Count().IsEqualTo(1);
  }

  /// <summary>Projecting a field reads it from a row already selected; it does not drive the scan.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Projection_OfJsonOnlyField_NoDiagnosticAsync() {
    var source = _repositoryOver("""
            return _rows.Select(r => r.Data.JsonOnly).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>The row's own columns are not model JSON.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnRowKey_NoDiagnosticAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Id == id).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>A queryable that is not a perspective is none of this analyzer's business.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnNonPerspectiveQueryable_NoDiagnosticAsync() {
    var source = """
      using System;
      using System.Linq;

      namespace TestApp;

      public class Holder {
        public string Name { get; init; } = string.Empty;
      }

      public class Wrapper {
        public Holder Data { get; init; } = new();
      }

      public class PlainRepository {
        private readonly IQueryable<Wrapper> _rows = null!;

        public object Find() => _rows.Where(r => r.Data.Name == "x").ToList();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  // ========================================
  // Opting out
  // ========================================

  /// <summary>The opt-out on the property exempts that one field.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_WithSuppressionOnProperty_NoDiagnosticAsync() {
    var source = _suppressedModelSource(
      propertyAttribute: """[SuppressIndexAdvisory("a handful of rows by construction")]""");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>The opt-out on the model exempts every field of that perspective.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_WithSuppressionOnModel_NoDiagnosticAsync() {
    var source = _suppressedModelSource(
      modelAttribute: """[SuppressIndexAdvisory("bounded by the retention cap")]""");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>The assembly-wide opt-out is the blunt instrument, and it works.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_WithSuppressionOnAssembly_NoDiagnosticAsync() {
    var source = _suppressedModelSource(
      assemblyAttribute: """[assembly: SuppressIndexAdvisory("this host queries reference tables only")]""");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>
  /// A blank reason is not a decision, so it does not suppress. The attribute's whole value is the
  /// stated rationale, and a placeholder would turn it back into a pragma.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("\"\"")]
  [Arguments("\"   \"")]
  public async Task Filter_WithBlankSuppressionReason_StillReportsAsync(string reasonLiteral) {
    var source = _suppressedModelSource(
      propertyAttribute: $"[SuppressIndexAdvisory({reasonLiteral})]");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).Count().IsEqualTo(1);
  }

  /// <summary>A suppression on one field says nothing about the next one.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_WithSuppressionOnAnotherProperty_StillReportsAsync() {
    var source = """
      using System;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public class PairModel {
        [SuppressIndexAdvisory("this one is fine")]
        public string Excused { get; init; } = string.Empty;

        public string NotExcused { get; init; } = string.Empty;
      }

      public class PairRepository {
        private readonly IQueryable<PerspectiveRow<PairModel>> _rows = null!;

        public object Find() =>
          _rows.Where(r => r.Data.Excused.Contains("a") && r.Data.NotExcused.Contains("b")).ToList();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    var reported = _whiz302(diagnostics).ToList();
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("NotExcused");
  }

  private static string _suppressedModelSource(
      string propertyAttribute = "",
      string modelAttribute = "",
      string assemblyAttribute = "") =>
    $$"""
      using System;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Perspectives;

      {{assemblyAttribute}}

      namespace TestApp;

      {{modelAttribute}}
      public class SmallModel {
        {{propertyAttribute}}
        public string Region { get; init; } = string.Empty;
      }

      public class SmallRepository {
        private readonly IQueryable<PerspectiveRow<SmallModel>> _rows = null!;

        public object Find() => _rows.Where(r => r.Data.Region.Contains("ab")).ToList();
      }
      """;

  // ========================================
  // Refocus: equality is already indexed, so the advisory is about what containment cannot serve
  // ========================================

  /// <summary>
  /// An equality filter on a JSON-only scalar compiles to a containment test the GIN index answers,
  /// so telling the author to promote the field would be wrong as well as noisy.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("r.Data.JsonOnly == \"x\"")]
  [Arguments("\"x\" == r.Data.JsonOnly")]
  [Arguments("r.Data.AlsoJsonOnly == id")]
  [Arguments("r.Data.Num == 1")]
  [Arguments("r.Data.Big == 1L")]
  [Arguments("r.Data.Small == (short)1")]
  [Arguments("r.Data.Money == 1.5m")]
  [Arguments("r.Data.Flag == true")]
  [Arguments("r.Data.JsonOnly.Equals(\"x\", System.StringComparison.Ordinal)")]
  [Arguments("r.Data.Dbl == 1.5")]
  [Arguments("r.Data.Single == 1.5f")]
  [Arguments("r.Data.State == Mood.High")]
  [Arguments("r.Data.When == when")]
  public async Task EqualityContainmentCanServe_IsNotReportedAsync(string predicate) {
    var source = _repositoryOver($"""
            var when = System.DateTime.UnixEpoch;
            return _rows.Where(r => {predicate}).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>
  /// Everything containment cannot express still forces a scan, and those are exactly the filters
  /// the advisory now exists for.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("r.Data.Num > 1")]
  [Arguments("r.Data.Num >= 1")]
  [Arguments("r.Data.Num < 1")]
  [Arguments("r.Data.Num <= 1")]
  [Arguments("r.Data.JsonOnly != \"x\"")]
  [Arguments("!(r.Data.JsonOnly == \"x\")")]
  [Arguments("r.Data.JsonOnly == null")]
  [Arguments("r.Data.JsonOnly.Contains(\"ab\")")]
  [Arguments("r.Data.JsonOnly.StartsWith(\"ab\")")]
  [Arguments("r.Data.JsonOnly.Equals(\"x\", System.StringComparison.OrdinalIgnoreCase)")]
  [Arguments("r.Data.WhenOffset == offset")]
  [Arguments("r.Data.When > when")]
  public async Task ShapesContainmentCannotServe_AreStillReportedAsync(string predicate) {
    var source = _repositoryOver($"""
            var when = System.DateTime.UnixEpoch;
            var offset = System.DateTimeOffset.UnixEpoch;
            return _rows.Where(r => {predicate}).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsNotEmpty();
  }

  /// <summary>Ordering needs the value itself, which containment never supplies.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task OrderingOnAJsonOnlyField_IsStillReportedAsync() {
    var source = _repositoryOver("""
            return _rows.OrderBy(r => r.Data.JsonOnly).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsNotEmpty();
  }

  /// <summary>
  /// A compound predicate reports only the half containment cannot serve, so the author is pointed
  /// at the field that actually needs a column.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task CompoundPredicate_ReportsOnlyTheUnservedHalfAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.JsonOnly == "x" && r.Data.Num > 1).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    var reported = _whiz302(diagnostics).ToList();
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Num");
  }
}
