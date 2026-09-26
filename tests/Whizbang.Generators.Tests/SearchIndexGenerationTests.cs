using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// <c>[Indexed(IndexKinds.Search)]</c> builds a trigram index over the framework's fold of the value and tells
/// the runtime, so a <c>Contains</c> on the field is translated to the same fold and served by the index.
/// </summary>
/// <remarks>
/// The index expression must be exactly what the query produces or the planner does not use it, which is
/// why both sides go through one function, <c>wh_fold</c>. Nothing extra is stored: the index is built
/// over the document, so declaring the kind on a model that already has rows needs no data migration.
/// </remarks>
public class SearchIndexGenerationTests {

  private const string MODEL = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record TestEvent : IEvent;

    public record JobModel {
      [StreamId]
      public Guid Id { get; init; }

      [Indexed(IndexKinds.Search)]
      public string JobName { get; init; } = "";

      [Indexed(IndexKinds.Search)]
      public int Grade { get; init; }

      [PhysicalField]
      [Indexed(IndexKinds.Search)]
      public string? Code { get; init; }
    }

    public class JobPerspective : IPerspectiveFor<JobModel, TestEvent> {
      public JobModel Apply(JobModel currentData, TestEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _outputAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    return string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
  }

  [Test]
  public async Task ASearchField_GetsATrigramIndexOverItsFoldedValueAsync() {
    var output = await _outputAsync();

    await Assert.That(output).Contains("_jobname_fold_trgm", StringComparison.Ordinal);
    await Assert.That(output).Contains("USING gin (\"\"testapp\"\".wh_fold(data ->> 'JobName') gin_trgm_ops)", StringComparison.Ordinal)
      .Because("the index must be over the very expression the query produces: the fold of the raw value");
  }

  [Test]
  public async Task TheSearchIndex_IsInsideTheOptionalTrigramBlockAsync() {
    var output = await _outputAsync();

    var begin = output.IndexOf("-- @whizbang:optional-extension pg_trgm", StringComparison.Ordinal);
    var index = output.IndexOf("_jobname_fold_trgm", StringComparison.Ordinal);
    var end = output.IndexOf("-- @whizbang:optional-extension-end", begin + 1, StringComparison.Ordinal);

    await Assert.That(begin).IsGreaterThan(-1);
    await Assert.That(index).IsGreaterThan(begin);
    await Assert.That(end).IsGreaterThan(index)
      .Because("a server that refuses the trigram extension skips the index with a warning instead of failing to start");
  }

  [Test]
  public async Task TheRuntime_IsToldTheFieldIsASearchFieldAsync() {
    var output = await _outputAsync();

    await Assert.That(output).Contains("JsonIndexRegistry.Register<global::TestApp.JobModel>(\"JobName\", Whizbang.Core.Perspectives.IndexKinds.Search)", StringComparison.Ordinal)
      .Because("the query side rewrites Contains only for a field it knows was indexed for search");
  }

  [Test]
  public async Task ASearchDeclarationOnANonTextField_BuildsNothingAsync() {
    var output = await _outputAsync();

    await Assert.That(output).DoesNotContain("_grade_fold_trgm", StringComparison.Ordinal)
      .Because("folding means nothing over a number; the declaration analyzer reports it instead");
  }

  [Test]
  public async Task APromotedSearchField_GetsAFoldIndexOverItsColumn_InsideTheTrigramBlockAsync() {
    var output = await _outputAsync();

    var index = output.IndexOf("_code_fold_trgm ON", StringComparison.Ordinal);
    await Assert.That(index).IsGreaterThan(-1);
    await Assert.That(output).Contains("USING gin (\"\"testapp\"\".wh_fold(code) gin_trgm_ops)", StringComparison.Ordinal)
      .Because("a promoted field is searched on its column, so the index folds the column");
    var begin = output.LastIndexOf("-- @whizbang:optional-extension pg_trgm", index, StringComparison.Ordinal);
    var end = output.IndexOf("-- @whizbang:optional-extension-end", index, StringComparison.Ordinal);
    await Assert.That(begin).IsGreaterThan(-1);
    await Assert.That(end).IsGreaterThan(index);
  }

  [Test]
  public async Task APromotedSearchOnlyField_GetsNoPlainBtreeAsync() {
    var output = await _outputAsync();

    await Assert.That(output).DoesNotContain("idx_job_code ON", StringComparison.Ordinal)
      .Because("the field asked for search only; an ordered index over it would be write cost for nothing");
  }

  [Test]
  public async Task APromotedSearchField_IsRegisteredForTheRewriteAsync() {
    var output = await _outputAsync();

    await Assert.That(output).Contains("JsonIndexRegistry.Register<global::TestApp.JobModel>(\"Code\", Whizbang.Core.Perspectives.IndexKinds.Search)", StringComparison.Ordinal);
  }

  private const string SEARCH_ONLY_MODEL = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record TestEvent : IEvent;

    public record TitleModel {
      [StreamId]
      public Guid Id { get; init; }

      [Indexed(IndexKinds.Search)]
      public string Title { get; init; } = "";
    }

    public class TitlePerspective : IPerspectiveFor<TitleModel, TestEvent> {
      public TitleModel Apply(TitleModel currentData, TestEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  [Test]
  public async Task ASearchOnlyModel_InstallsTheQueryRewrite_OnEveryRegistrationPathAsync() {
    // The turnkey path (AddWhizbang().WithEFCore<T>()) and the generated Add{Context} extension register the
    // context separately. A model whose only special field is a Search field has no promoted column, but its
    // Contains still has to be rewritten to the fold, so both must install the rewrite; missing it on one path
    // leaves search unfolded (case- and quote-sensitive) and scanning, silently.
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(SEARCH_ONLY_MODEL);
    var registrations = result.GeneratedSources
      .Select(s => s.SourceText.ToString())
      .Where(s => s.Contains("DbContextRegistrationRegistry.Register<", StringComparison.Ordinal)
        || s.Contains("public static IServiceCollection Add", StringComparison.Ordinal))
      .ToList();

    await Assert.That(registrations.Count).IsGreaterThanOrEqualTo(2);
    foreach (var source in registrations) {
      await Assert.That(source).Contains(".UseWhizbangPhysicalFields();", StringComparison.Ordinal);
      await Assert.That(source).Contains("using Whizbang.Data.EFCore.Postgres.QueryTranslation;", StringComparison.Ordinal)
        .Because("the call is emitted unqualified, so its file needs the namespace or the consumer does not compile");
    }
  }
}
