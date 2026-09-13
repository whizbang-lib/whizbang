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

        [PhysicalField]
        [Indexed]
        public Guid IndexedOwnerId { get; init; }

        [PhysicalField(Unique = true)]
        public string UniqueCode { get; init; } = string.Empty;

        [PhysicalField]
        public string PromotedButNotIndexed { get; init; } = string.Empty;

        [PhysicalField]
        [Indexed]
        public string PromotedAndIndexed { get; init; } = string.Empty;

        [VectorField(8)]
        [Indexed]
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

        public DateOnly Day { get; init; }

        public TimeOnly Clock { get; init; }

        public TimeSpan Elapsed { get; init; }

        [Indexed]
        public int DeclaredBtree { get; init; }

        [Indexed(IndexKinds.Substring)]
        public string DeclaredTrigram { get; init; } = string.Empty;

        [Indexed]
        public string DeclaredSensitive { get; init; } = string.Empty;

        [Indexed(caseInsensitive: true)]
        public string DeclaredFolded { get; init; } = string.Empty;

        [Indexed]
        [Indexed(caseInsensitive: true)]
        public string DeclaredBothWays { get; init; } = string.Empty;

        [Indexed(IndexKinds.Substring, caseInsensitive: true)]
        public string DeclaredFoldedSubstring { get; init; } = string.Empty;
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
    const string source = PRELUDE + """
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
    const string source = """
      using System;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public class RawModel {
        [VectorField(8)]
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
    const string source = """
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
    const string source = """
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
  // Equality on the date family moved to this side when its stored form became a number. The value
  // is stored converted, so the filter compiles to an extraction against that number rather than to
  // a containment test, which is correct and, without a declared index, a scan. Saying so is the
  // whole point: the alternative is a silent sequential scan on a filter that used to be a lookup.
  // A declared index is also the better outcome, since a single-column btree probe beats containment,
  // which reads the document index and then rechecks every candidate row.
  [Arguments("r.Data.When == when")]
  [Arguments("r.Data.Day == day")]
  [Arguments("r.Data.Clock == clock")]
  [Arguments("r.Data.Elapsed == elapsed")]
  public async Task ShapesContainmentCannotServe_AreStillReportedAsync(string predicate) {
    var source = _repositoryOver($"""
            var when = System.DateTime.UnixEpoch;
            var offset = System.DateTimeOffset.UnixEpoch;
            var day = new System.DateOnly(2026, 3, 4);
            var clock = new System.TimeOnly(5, 6, 7);
            var elapsed = System.TimeSpan.FromMinutes(3);
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

  // ========================================
  // The advice matches how the model is stored
  // ========================================

  private const string POLYMORPHIC_PRELUDE = """
      using System;
      using System.Collections.Generic;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public abstract class PaymentMethod {
        public string Name { get; init; } = string.Empty;
      }

      public class ThingModel {
        [StreamId]
        public Guid ThingId { get; init; }

        public PaymentMethod? Payment { get; init; }

        public string JsonOnly { get; init; } = string.Empty;
      }

      """;

  /// <summary>
  /// On a model stored as one serialized value, the advice offers the column and not the JSON index.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Both fixes are named in the ordinary message, and on such a model one of them does not work:
  /// the document is not mapped property by property, so an index over an extraction from it is
  /// unreachable and the generator skips it. Offering it here would send the author to a build that
  /// reports WHIZ304 for taking the advice.
  /// </para>
  /// <para>
  /// This is the pairing that makes the family coherent. WHIZ302 says a filter scans, WHIZ304 says an
  /// index over this model cannot be reached, and an author who follows the first must not land on
  /// the second.
  /// </para>
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnOpaquelyStoredModel_OffersTheColumnNotTheJsonIndexAsync() {
    const string source = POLYMORPHIC_PRELUDE + """
      public class ThingRepository {
        private readonly IQueryable<PerspectiveRow<ThingModel>> _rows = null!;

        public object Find(Guid id) {
          return _rows.Where(r => r.Data.JsonOnly.Contains("ab")).ToList();
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);
    var message = _whiz302(diagnostics).Single().GetMessage(CultureInfo.InvariantCulture);

    await Assert.That(message).Contains("PhysicalField", StringComparison.Ordinal)
      .Because("a promoted column is a real column and stays reachable however the rest of the "
        + "document is stored, which makes it the fix that works here");
    await Assert.That(message).DoesNotContain("Mark it [Indexed]", StringComparison.Ordinal)
      .Because("an index over this model's document is skipped and reported by WHIZ304, so offering "
        + "it alone would send the author into the other diagnostic for following the advice; on "
        + "this model the attribute only works alongside the promotion");
  }

  /// <summary>
  /// On an ordinary model both fixes are still offered, since both work.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_OnMappedModel_StillOffersTheJsonIndexAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.JsonOnly.Contains("ab")).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);
    var message = _whiz302(diagnostics).Single().GetMessage(CultureInfo.InvariantCulture);

    await Assert.That(message).Contains("[Indexed]", StringComparison.Ordinal)
      .Because("it is the cheaper of the two fixes wherever it works, and narrowing the advice for "
        + "one kind of model must not narrow it for every model");
  }

  // ========================================
  // A declared index is an index
  // ========================================

  /// <summary>
  /// A field that declares its own btree is not reported, for any shape a btree serves.
  /// </summary>
  /// <remarks>
  /// The advisory tells an author to mark the field <c>[Indexed]</c>. If it kept reporting after
  /// they did, the advice would be a loop with no exit, and the only way out would be to suppress a
  /// warning that was telling the truth before the fix and a lie after it.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("r.Data.DeclaredBtree > 1")]
  [Arguments("r.Data.DeclaredBtree >= 1")]
  [Arguments("r.Data.DeclaredBtree < 1")]
  [Arguments("r.Data.DeclaredBtree != 1")]
  public async Task ADeclaredBtree_IsNotReportedAsync(string predicate) {
    var source = _repositoryOver($"""
            return _rows.Where(r => {predicate}).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("the field carries the index this advisory asks for, so reporting it again would "
        + "leave the author with no way to satisfy the advice");
  }

  /// <summary>Ordering on a declared btree is not reported either, since that is what it serves.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task OrderingOnADeclaredBtree_IsNotReportedAsync() {
    var source = _repositoryOver("""
            return _rows.OrderBy(r => r.Data.DeclaredBtree).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>
  /// A trigram declaration covers substring matching, which is the thing a trigram index answers.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("r.Data.DeclaredTrigram.Contains(\"ab\")")]
  [Arguments("r.Data.DeclaredTrigram.StartsWith(\"ab\")")]
  [Arguments("r.Data.DeclaredTrigram.EndsWith(\"ab\")")]
  public async Task ADeclaredTrigram_IsNotReportedForSubstringMatchingAsync(string predicate) {
    var source = _repositoryOver($"""
            return _rows.Where(r => {predicate}).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("substring matching is exactly what a trigram index answers, so the field it is "
        + "declared on is not scanning");
  }

  /// <summary>
  /// A trigram declaration does not cover an ordering, because a trigram index cannot serve one.
  /// </summary>
  /// <remarks>
  /// The distinction matters in the direction that costs: treating any declaration as covering any
  /// shape would silence the advisory on a filter that really does scan, which is the failure this
  /// whole area exists to prevent.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ADeclaredTrigram_IsStillReportedForAnOrderingAsync() {
    var source = _repositoryOver("""
            return _rows.OrderBy(r => r.Data.DeclaredTrigram).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsNotEmpty()
      .Because("a trigram index answers substring matching and nothing else, so an ordering on the "
        + "field still reads every row");
  }

  /// <summary>
  /// A promoted field indexed by the universal attribute is not reported.
  /// </summary>
  /// <remarks>
  /// The same advice loop as the document case: the diagnostic asks for an index, so it has to stop
  /// asking once one is declared, whichever side of the promotion the field sits on.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task APromotedAndIndexedField_IsNotReportedAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.PromotedAndIndexed.Contains("ab")).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("[PhysicalField] promotes it and [Indexed] indexes the column, so the filter is a "
        + "lookup and there is nothing left to advise");
  }

  // ========================================
  // Case folding: the index has to be over the expression the comparison produces
  // ========================================

  /// <summary>
  /// A comparison that folds case is reported even on a declared field, because the index built over
  /// the stored value cannot answer a comparison over the folded one.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The fold is part of the expression, so an index over the extraction and an index over the folded
  /// extraction are two different indexes and neither answers the other's query. This is the failure
  /// that is invisible without the check: the author declared an index, can see it in the database,
  /// and every one of these queries still reads every row.
  /// </para>
  /// <para>
  /// The advice has to name the fold, because the author has already taken the advice this diagnostic
  /// gives by default.
  /// </para>
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("ToLower")]
  public async Task Filter_FoldingCase_OnAnUnfoldedDeclaration_ReportsAsync(string fold) {
    var source = _repositoryOver($$"""
            return _rows.Where(r => r.Data.DeclaredSensitive.{{fold}}() == "ab").ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);
    var reported = _whiz302(diagnostics).ToList();

    await Assert.That(reported).HasSingleItem()
      .Because("an index over the stored value cannot answer a comparison over the folded value, so "
        + "this filter reads every row despite the declaration");
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture))
      .Contains("caseInsensitive: true", StringComparison.Ordinal)
      .Because("the author already marked the field [Indexed], so repeating that advice has no exit");
  }

  /// <summary>A folded declaration answers the folded comparison, which is what it is for.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("ToLower")]
  public async Task Filter_FoldingCase_OnAFoldedDeclaration_IsNotReportedAsync(string fold) {
    var source = _repositoryOver($$"""
            return _rows.Where(r => r.Data.DeclaredFolded.{{fold}}() == "ab").ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("the index is built over the folded value, which is the expression this comparison "
        + "produces, so the filter is a lookup");
  }

  /// <summary>
  /// A folded declaration does not answer a comparison that respects case, so that is still reported.
  /// </summary>
  /// <remarks>
  /// The mirror of the case above, and the reason the two declarations are separate rather than one
  /// index serving both. Written as a range because plain equality is answered by the document's
  /// containment index, which would make this silent for a reason that has nothing to do with the
  /// fold.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_RespectingCase_OnAFoldedDeclaration_ReportsAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.DeclaredFolded.CompareTo("m") > 0).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).HasSingleItem()
      .Because("the only index on the field is over the folded value, and a range that respects case "
        + "cannot be answered from it");
  }

  /// <summary>
  /// A field declared both ways answers both comparisons, which is what declaring it twice buys.
  /// </summary>
  /// <remarks>
  /// The guard on the fix above. Matching the comparison's fold against a single answer for the whole
  /// field would silence one form and report the other, and a field filtered both ways is the
  /// ordinary case rather than the exotic one.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("r.Data.DeclaredBothWays.ToLower() == \"ab\"")]
  [Arguments("r.Data.DeclaredBothWays.CompareTo(\"m\") > 0")]
  public async Task Filter_OnAFieldDeclaredBothWays_IsNotReportedAsync(string filter) {
    var source = _repositoryOver($$"""
            return _rows.Where(r => {{filter}}).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("both indexes exist, so whichever way the comparison folds there is one over the "
        + "expression it produces");
  }

  /// <summary>
  /// Folding upward is reported whatever is declared, because the framework indexes the lower fold.
  /// </summary>
  /// <remarks>
  /// <c>ToUpper</c> compiles to <c>upper(...)</c>, and an index over <c>lower(...)</c> is no more use
  /// to it than an index over the stored value. One fold has to be the one that is built, so the
  /// message says which, rather than letting the query look served when it is not.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("ToUpper")]
  public async Task Filter_FoldingUpward_ReportsAndNamesTheFoldThatIsIndexedAsync(string fold) {
    var source = _repositoryOver($$"""
            return _rows.Where(r => r.Data.DeclaredBothWays.{{fold}}() == "AB").ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);
    var reported = _whiz302(diagnostics).ToList();

    await Assert.That(reported).HasSingleItem()
      .Because("no index the attribute can ask for is built over the upper fold, so this reads every "
        + "row however the field is declared");
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture))
      .Contains("ToLower", StringComparison.Ordinal)
      .Because("the fix is to compare with the fold the index is built over, so the message has to "
        + "name it");
  }

  /// <summary>
  /// A fold that never becomes SQL is not treated as one, because there is no plan to advise about.
  /// </summary>
  /// <remarks>
  /// Entity Framework maps the parameterless <c>ToLower</c> and <c>ToUpper</c> and has no mapping for
  /// the invariant forms or the ones taking a culture: a query written with those fails to translate
  /// rather than scanning. Reading them as folds would attach index advice to a query that never
  /// reaches the database, and would quietly start reporting fields whose declarations are right.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("ToLowerInvariant")]
  [Arguments("ToUpperInvariant")]
  public async Task Filter_FoldingThatDoesNotTranslate_IsNotTreatedAsAFoldAsync(string fold) {
    var source = _repositoryOver($$"""
            return _rows.Where(r => r.Data.DeclaredSensitive.{{fold}}() == "ab").ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("this query does not run at all, so an index advisory on it would be advice about a "
        + "plan that never exists");
  }

  /// <summary>
  /// A substring match that folds case is served by a declaration that asks for both.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is what a case-insensitive search looks like in practice, and it reads through the fold:
  /// the operator is applied to the folded value, so the shape is <c>field.ToLower().Contains(…)</c>
  /// and the field the index is over is two calls away rather than one.
  /// </para>
  /// <para>
  /// Worth its own test because missing it fails in the direction that wastes people's time. The
  /// author declares exactly the right thing, the index is built, the query uses it, and the advisory
  /// goes on reporting the line: advice with no exit, on the most common shape there is.
  /// </para>
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_FoldedSubstringMatch_OnAFoldedSubstringDeclaration_IsNotReportedAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.DeclaredFoldedSubstring.ToLower().Contains("ab")).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("the index answers pattern matching and is built over the folded value, which is "
        + "both of the things this comparison needs");
  }

  /// <summary>
  /// A folded substring match is not served by a substring index over the stored value.
  /// </summary>
  /// <remarks>
  /// The pair to the case above, and the reason it cannot simply read through the fold and forget it:
  /// the fold still has to match, or the advisory would silence the one shape that made the
  /// case-insensitive declaration necessary.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_FoldedSubstringMatch_OnAnUnfoldedSubstringDeclaration_ReportsAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.DeclaredTrigram.ToLower().Contains("ab")).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).HasSingleItem()
      .Because("the index is over the stored value, and this pattern is matched against the folded "
        + "one, so the planner has nothing to use");
  }

  /// <summary>
  /// A substring match that respects case is not served by a folded substring index.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Filter_SubstringMatch_OnAFoldedSubstringDeclaration_ReportsAsync() {
    var source = _repositoryOver("""
            return _rows.Where(r => r.Data.DeclaredFoldedSubstring.Contains("ab")).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).HasSingleItem()
      .Because("the only index on the field is over the folded value, and a pattern matched against "
        + "the stored one cannot be answered from it");
  }

  /// <summary>
  /// A substring match is not served by an ordered declaration, in either direction.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The mirror of the case above it, which has always held: a substring index answers pattern
  /// matching and nothing else, so an ordering on such a field still reports. The reverse was not
  /// checked, and an ordered declaration silenced every shape including the one it cannot answer. An
  /// ordered index over text is no use to a pattern match with a leading wildcard, and none to a
  /// prefix match either under any ordinary collation.
  /// </para>
  /// <para>
  /// It is the same false negative the folding work removed, one axis over: a field carrying an index
  /// looked served by every query on it.
  /// </para>
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("Contains")]
  [Arguments("StartsWith")]
  [Arguments("EndsWith")]
  public async Task Filter_SubstringMatch_OnAnOrderedDeclaration_ReportsAsync(string op) {
    var source = _repositoryOver($$"""
            return _rows.Where(r => r.Data.DeclaredSensitive.{{op}}("ab")).ToList();
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).HasSingleItem()
      .Because("an ordered index answers ranges and orderings, and a pattern match is answered by "
        + "neither, so this filter still reads every row");
  }

  /// <summary>An ordered declaration still serves the shapes it is for.</summary>
  /// <remarks>
  /// The guard on the fix above. Narrowing what an ordered declaration covers must not start
  /// reporting the ranges, orderings, equality and null tests it exists to answer.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("_rows.Where(r => r.Data.DeclaredSensitive.CompareTo(\"m\") > 0).ToList()")]
  [Arguments("_rows.OrderBy(r => r.Data.DeclaredSensitive).ToList()")]
  [Arguments("_rows.Where(r => r.Data.DeclaredSensitive == null).ToList()")]
  [Arguments("_rows.Where(r => r.Data.DeclaredBtree > 3).ToList()")]
  public async Task Filter_OrderedShapes_OnAnOrderedDeclaration_AreNotReportedAsync(string query) {
    var source = _repositoryOver($$"""
            return {{query}};
      """);

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("these are exactly what an ordered index answers, so the declaration serves them");
  }
}
