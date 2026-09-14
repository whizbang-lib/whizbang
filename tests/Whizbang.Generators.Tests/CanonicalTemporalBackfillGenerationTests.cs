using TUnit.Assertions.Extensions;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That the rewrite into the canonical temporal form reaches the schema, ahead of the index built
/// over its result.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is not convention. With the numeric form PostgreSQL refuses to build the index while
/// any row is still a string, so emitted the other way round a partial conversion would leave an
/// index over a column about to change underneath it. Order alone is not enough either: the rewrite
/// has to be committed before the index is built, which is what the boundary between them is for.
/// </para>
/// <para>
/// The statements themselves are proven against a real database in
/// <c>CanonicalTemporalBackfillTests</c>. What is asserted here is that they are emitted at all, for
/// the right models, in the right order.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
public class CanonicalTemporalBackfillGenerationTests {
  private const string TEMPORAL_MODEL = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record Reported : IEvent;

    public record ReportModel {
      [StreamId]
      public Guid ReportId { get; init; }

      [Indexed]
      public DateTime OccurredAt { get; init; }

      public TimeSpan Elapsed { get; init; }

      public string Label { get; init; } = string.Empty;
    }

    public class ReportPerspective : IPerspectiveFor<ReportModel, Reported> {
      public ReportModel Apply(ReportModel currentData, Reported eventData) => currentData;
    }

    [WhizbangDbContext]
    public class ReportDbContext : DbContext {
      public ReportDbContext(DbContextOptions<ReportDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _generatedAsync(string source) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    return string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
  }

  /// <summary>Each temporal key gets its rewrite.</summary>
  [Test]
  [Arguments("OccurredAt")]
  [Arguments("Elapsed")]
  public async Task ATemporalKeyIsRewrittenInTheSchemaAsync(string key) {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    await Assert.That(output).Contains($"jsonb_typeof(data -> '{key}') = 'string'",
      StringComparison.Ordinal)
      .Because("a row written before the format changed still holds a rendering, and a column "
        + "holding both forms can neither be indexed nor range-scanned correctly");
  }

  /// <summary>
  /// The rewrite comes before the index over what it produced.
  /// </summary>
  /// <remarks>
  /// PostgreSQL evaluates the index expression for every heap tuple that is not yet dead, so an
  /// index built over a key while a row is still a string refuses to build. The other order would
  /// leave an index over a column about to change underneath it.
  /// </remarks>
  [Test]
  public async Task TheRewriteComesBeforeTheIndexAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    var rewrite = output.IndexOf("jsonb_typeof(data -> 'OccurredAt') = 'string'",
      StringComparison.Ordinal);
    var index = output.IndexOf("'OccurredAt')::bigint", StringComparison.Ordinal);

    await Assert.That(rewrite).IsGreaterThan(-1);
    await Assert.That(index).IsGreaterThan(-1);
    await Assert.That(rewrite).IsLessThan(index)
      .Because("an index over a rewritten key cannot be built before the rewrite that produced it");
  }

  /// <summary>
  /// A commit boundary sits between the rewrite and the index built over its result.
  /// </summary>
  /// <remarks>
  /// Ordering the two is necessary and not sufficient. A superseded row version stays live until the
  /// rewrite commits, and the index build evaluates its expression over live tuples, so an index
  /// built in the rewriting transaction meets the values as they were before it. The rollback then
  /// undoes the rewrite along with the index and every retry begins from the state that just failed,
  /// which is a schema that can never finish migrating rather than a startup that failed once.
  /// </remarks>
  [Test]
  public async Task ACommitBoundarySeparatesTheRewriteFromTheIndexAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    var rewrite = output.IndexOf("jsonb_typeof(data -> 'OccurredAt') = 'string'",
      StringComparison.Ordinal);
    var boundary = output.IndexOf(CanonicalTemporalBackfillSql.COMMIT_BOUNDARY,
      StringComparison.Ordinal);
    var index = output.IndexOf("'OccurredAt')::bigint", StringComparison.Ordinal);

    await Assert.That(boundary).IsGreaterThan(rewrite)
      .Because("the boundary commits the rewrite, so it has to come after it");
    await Assert.That(boundary).IsLessThan(index)
      .Because("the index is the statement that needs the rewrite committed");
  }

  /// <summary>
  /// A model with no temporal property gets no rewrite, so nothing is paid for nothing.
  /// </summary>
  [Test]
  public async Task AModelWithNoTemporalPropertyGetsNoRewriteAsync() {
    var output = await _generatedAsync("""
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record Reported : IEvent;

      public record ReportModel {
        [StreamId]
        public Guid ReportId { get; init; }

        public string Label { get; init; } = string.Empty;
        public int Count { get; init; }
      }

      public class ReportPerspective : IPerspectiveFor<ReportModel, Reported> {
        public ReportModel Apply(ReportModel currentData, Reported eventData) => currentData;
      }

      [WhizbangDbContext]
      public class ReportDbContext : DbContext {
        public ReportDbContext(DbContextOptions<ReportDbContext> options) : base(options) { }
      }
      """);

    await Assert.That(output).DoesNotContain("jsonb_typeof(data -> ", StringComparison.Ordinal)
      .Because("a model with nothing to convert should not carry a statement that scans its table "
        + "at every startup to discover there is nothing to do");

    // And no boundary either. The boundary takes schema work out of the initializer's transaction,
    // which is worth doing only for the dependency that needs it.
    await Assert.That(output).DoesNotContain(
      CanonicalTemporalBackfillSql.COMMIT_BOUNDARY, StringComparison.Ordinal);
  }

  /// <summary>
  /// The emitted SQL carries no brace, because braces do not survive the trip into generated C#.
  /// </summary>
  /// <remarks>
  /// The schema text is embedded into a C# string where <c>{</c> and <c>}</c> are doubled for the
  /// formatter, so a path literal or a regular-expression quantifier would reach the database
  /// mangled. Worth a test rather than a comment: the failure is at a customer's startup, on SQL
  /// that reads correctly in the source.
  /// </remarks>
  [Test]
  public async Task TheRewriteCarriesNoBraceAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    var start = output.IndexOf("UPDATE", StringComparison.Ordinal);
    await Assert.That(start).IsGreaterThan(-1);

    var statement = output[start..output.IndexOf(';', start)];

    await Assert.That(statement).DoesNotContain("{", StringComparison.Ordinal)
      .Because("a brace is doubled on its way into the generated source, so SQL that reads "
        + "correctly here would arrive at the database mangled");
  }
}
