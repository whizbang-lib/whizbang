using TUnit.Assertions.Extensions;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// WHIZ306: a model a request can sort by, whose fields carry no index.
/// </summary>
/// <remarks>
/// <para>
/// The exposure is the evidence. A predicate written in source is WHIZ302's business; one composed
/// when a request arrives writes nothing down, so the only durable trace is the attribute that put
/// the middleware there. These tests fix what that attribute has to look like, where the model is
/// found from each surface shape, and when saying nothing is the right answer.
/// </para>
/// <para>
/// The silent cases carry as much weight as the reported one. A diagnostic that fires on a model
/// already fully indexed, or on a surface offering neither sorting nor an expression, is noise, and
/// noise on a warning that costs real money to act on gets the whole rule suppressed.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
public class QueryExposureIndexAnalyzerTests {
  private const string PREAMBLE = """
    using System;
    using System.Collections.Generic;
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

    [ComposesQueryFromRequest(QueryExposure.Filtering)]
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class FilterOnlyAttribute : Attribute { }

    public class WideModel {
      [StreamId]
      public Guid Id { get; init; }

      public string JobName { get; init; } = string.Empty;
      public string Status { get; init; } = string.Empty;
      public int Version { get; init; }
    }

    public class IndexedModel {
      [StreamId]
      public Guid Id { get; init; }

      [Indexed]
      public string JobName { get; init; } = string.Empty;
    }
    """;

  private static async Task<string[]> _whiz306Async(string source) {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<QueryExposureIndexAnalyzer>(
      PREAMBLE + Environment.NewLine + source);

    return [.. diagnostics
      .Where(d => d.Id == "WHIZ306")
      .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))];
  }

  /// <summary>A lens with sorting on, over a model with unindexed fields, is the reported case.</summary>
  [Test]
  public async Task ASortableLensOverAnUnindexedModelIsReportedAsync() {
    var messages = await _whiz306Async("""
      [Sortable]
      public interface IWideLens : ILensQuery<WideModel>;
      """);

    await Assert.That(messages.Length).IsEqualTo(1);
    await Assert.That(messages[0]).Contains("WideModel");
    await Assert.That(messages[0]).Contains("JobName");
    await Assert.That(messages[0]).Contains("3 of its fields")
      .Because("the count is what makes the warning actionable: it says how much is unaccounted for");
  }

  /// <summary>A resolver handing back a queryable is the other surface shape.</summary>
  [Test]
  [Arguments("IQueryable<WideModel>")]
  [Arguments("IQueryable<PerspectiveRow<WideModel>>")]
  public async Task ASortableResolverIsReportedAsync(string returnType) {
    var messages = await _whiz306Async($$"""
      public class Queries {
        [Sortable]
        public {{returnType}} GetWide() => throw new NotImplementedException();
      }
      """);

    await Assert.That(messages.Length).IsEqualTo(1);
    await Assert.That(messages[0]).Contains("WideModel");
  }

  /// <summary>A query surface written as a property is the same exposure.</summary>
  [Test]
  public async Task ASortablePropertyIsReportedAsync() {
    var messages = await _whiz306Async("""
      public class Queries {
        [Sortable]
        public IQueryable<WideModel> Wide => throw new NotImplementedException();
      }
      """);

    await Assert.That(messages.Length).IsEqualTo(1);
  }

  /// <summary>A model whose fields are all accounted for is not reported.</summary>
  [Test]
  public async Task AnIndexedModelIsNotReportedAsync() {
    var messages = await _whiz306Async("""
      [Sortable]
      public interface INarrowLens : ILensQuery<IndexedModel>;
      """);

    await Assert.That(messages).IsEmpty()
      .Because("every field has an answer, so there is nothing to tell the author");
  }

  /// <summary>An unmarked attribute is not an exposure.</summary>
  [Test]
  public async Task AnUnmarkedAttributeIsNotReportedAsync() {
    var messages = await _whiz306Async("""
      [AttributeUsage(AttributeTargets.Class)]
      public sealed class PlainAttribute : Attribute { }

      [Plain]
      public interface IPlainLens : ILensQuery<WideModel>;
      """);

    await Assert.That(messages).IsEmpty();
  }

  /// <summary>A lens with no attribute at all is not an exposure.</summary>
  [Test]
  public async Task AnUnexposedLensIsNotReportedAsync() {
    var messages = await _whiz306Async("""
      public interface IQuietLens : ILensQuery<WideModel>;
      """);

    await Assert.That(messages).IsEmpty();
  }

  /// <summary>
  /// Filtering alone is not reported, because containment can answer it.
  /// </summary>
  /// <remarks>
  /// The document's containment index serves a filter. It serves neither a sort nor a comparison over
  /// an extraction, which is why ordering is the line this diagnostic draws.
  /// </remarks>
  [Test]
  public async Task AFilterOnlyExposureIsNotReportedAsync() {
    var messages = await _whiz306Async("""
      [FilterOnly]
      public interface IFilterLens : ILensQuery<WideModel>;
      """);

    await Assert.That(messages).IsEmpty();
  }

  /// <summary>A surface with sorting turned off is not offering ordering.</summary>
  [Test]
  public async Task ASurfaceWithSortingOffIsNotReportedAsync() {
    var messages = await _whiz306Async("""
      [Sortable(EnableSorting = false)]
      public interface INoSortLens : ILensQuery<WideModel>;
      """);

    await Assert.That(messages).IsEmpty()
      .Because("reporting a surface that turns sorting off would be advice about something it does "
        + "not do");
  }

  /// <summary>A member that has already run its query cannot be ordered by a middleware.</summary>
  [Test]
  public async Task AMaterializedReturnIsNotReportedAsync() {
    var messages = await _whiz306Async("""
      public class Queries {
        [Sortable]
        public List<WideModel> GetAll() => throw new NotImplementedException();
      }
      """);

    await Assert.That(messages).IsEmpty()
      .Because("nothing can attach an ORDER BY to a list that has already been read");
  }

  /// <summary>A multi-model lens is reported once per model that needs it.</summary>
  [Test]
  public async Task AMultiModelLensIsReportedPerModelAsync() {
    var messages = await _whiz306Async("""
      [Sortable]
      public interface IPairLens : ILensQuery<WideModel, IndexedModel>;
      """);

    await Assert.That(messages.Length).IsEqualTo(1)
      .Because("both models are exposed, but only the one with unaccounted fields has anything to "
        + "report");
    await Assert.That(messages[0]).Contains("WideModel");
  }

  /// <summary>Two surfaces over one model report once each, on each surface.</summary>
  /// <remarks>
  /// Reported per exposure rather than per model on purpose: each surface is a separate decision an
  /// author can change, and squiggling only the first would hide the rest.
  /// </remarks>
  [Test]
  public async Task EachExposureOfAModelIsReportedAsync() {
    var messages = await _whiz306Async("""
      [Sortable]
      public interface IFirstLens : ILensQuery<WideModel>;

      [Sortable]
      public interface ISecondLens : ILensQuery<WideModel>;
      """);

    await Assert.That(messages.Length).IsEqualTo(2);
  }
}
