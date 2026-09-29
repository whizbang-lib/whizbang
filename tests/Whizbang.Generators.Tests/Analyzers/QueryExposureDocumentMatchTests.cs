using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions.Extensions;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// WHIZ307 and WHIZ308 on a surface that composes filtering from the request.
/// </summary>
/// <remarks>
/// <para>
/// A request-composed equality or <c>in</c> filter compiles to a whole-document match exactly as a
/// hand-written one does, but no source shows it, so the filter analyzer never sees it. The surface
/// is the evidence, as it is for WHIZ306: an attribute that puts filtering middleware there is a
/// statement that any field of the model can end up in such a filter.
/// </para>
/// <para>
/// That is the case that matters most for opting out of the whole-document index. Grid filters
/// arrive this way, and they are the queries a build-time check would otherwise miss entirely.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz307</docs>
public class QueryExposureDocumentMatchTests {
  private const string PREAMBLE = """
    using System;
    using System.Linq;
    using Whizbang.Core;
    using Whizbang.Core.Lenses;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    [ComposesQueryFromRequest]
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class SortableAttribute : Attribute {
      public bool EnableSorting { get; set; } = true;
      public bool EnableFiltering { get; set; } = true;
    }

    [ComposesQueryFromRequest(QueryExposures.Filtering)]
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class FilterOnlyAttribute : Attribute { }

    [PerspectiveQueries(MatchOnAnyField = false)]
    public class OptedOutModel {
      [StreamId]
      public Guid Id { get; init; }

      public string JobName { get; init; } = string.Empty;

      [Indexed]
      public string Status { get; init; } = string.Empty;
    }

    [PerspectiveQueries(MatchOnAnyField = false)]
    public class OptedOutIndexedModel {
      [StreamId]
      public Guid Id { get; init; }

      [Indexed]
      public string Status { get; init; } = string.Empty;
    }

    [PerspectiveQueries(MatchOnAnyField = true)]
    public class DeclaredModel {
      [StreamId]
      public Guid Id { get; init; }

      public string JobName { get; init; } = string.Empty;
    }

    public class UndeclaredModel {
      [StreamId]
      public Guid Id { get; init; }

      public string JobName { get; init; } = string.Empty;
    }
    """;

  private static async Task<List<Diagnostic>> _diagnosticsAsync(string source, string id) =>
    [.. (await AnalyzerTestHelper.GetDiagnosticsAsync<QueryExposureIndexAnalyzer>(PREAMBLE + Environment.NewLine + source))
      .Where(d => d.Id == id)];

  /// <summary>A filterable lens over a model that opted out, with an unindexed field, warns.</summary>
  [Test]
  public async Task AFilterableLensOverAnOptedOutModel_WarnsAsync() {
    var reported = await _diagnosticsAsync("""
      [FilterOnly]
      public interface IOptedOutLens : ILensQuery<OptedOutModel>;
      """, "WHIZ307");

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("'in' filter a request composes on 'OptedOutModel' (JobName)");
    await Assert.That(message).Contains("MatchOnAnyField = false");
  }

  /// <summary>A filterable resolver is the same exposure as a lens.</summary>
  [Test]
  public async Task AFilterableResolverOverAnOptedOutModel_WarnsAsync() {
    var reported = await _diagnosticsAsync("""
      public class Queries {
        [FilterOnly]
        public IQueryable<PerspectiveRow<OptedOutModel>> GetRows() => throw new NotImplementedException();
      }
      """, "WHIZ307");

    await Assert.That(reported).Count().IsEqualTo(1);
  }

  /// <summary>Sorting and filtering together raise both questions, each once.</summary>
  [Test]
  public async Task ASortableAndFilterableLens_RaisesBothAsync() {
    const string source = """
      [Sortable]
      public interface IOptedOutLens : ILensQuery<OptedOutModel>;
      """;

    await Assert.That(await _diagnosticsAsync(source, "WHIZ306")).Count().IsEqualTo(1);
    await Assert.That(await _diagnosticsAsync(source, "WHIZ307")).Count().IsEqualTo(1);
  }

  /// <summary>An undeclared model relies on the default index for composed filters, and is noted.</summary>
  [Test]
  public async Task AFilterableLensOverAnUndeclaredModel_IsNotedAsync() {
    var reported = await _diagnosticsAsync("""
      [FilterOnly]
      public interface IUndeclaredLens : ILensQuery<UndeclaredModel>;
      """, "WHIZ308");

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Info);
  }

  /// <summary>
  /// Nothing is said when the model keeps the index, when every field has an index of its own, or
  /// when the surface turned filtering off.
  /// </summary>
  [Test]
  [Arguments("[FilterOnly] public interface ILens : ILensQuery<DeclaredModel>;")]
  [Arguments("[FilterOnly] public interface ILens : ILensQuery<OptedOutIndexedModel>;")]
  [Arguments("[Sortable(EnableFiltering = false)] public interface ILens : ILensQuery<OptedOutModel>;")]
  public async Task NothingToSay_IsNotReportedAsync(string source) {
    await Assert.That(await _diagnosticsAsync(source, "WHIZ307")).IsEmpty();
    await Assert.That(await _diagnosticsAsync(source, "WHIZ308")).IsEmpty();
  }
}
