using TUnit.Assertions.Extensions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That the rewrite into the canonical temporal form reaches the schema, ahead of the index built
/// over its result.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is not convention. With the numeric form PostgreSQL refuses to build the index while
/// any row is still a string, so a rewrite that did not finish fails at the next statement rather
/// than leaving an index that is silently useless. Emitted the other way round, a partial conversion
/// would be invisible.
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

      [JsonIndexed]
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
  /// PostgreSQL evaluates the index expression for every row, so it refuses to build while any row
  /// is still a string. In this order an unfinished rewrite fails loudly at the next statement; in
  /// the other it would leave an index built over a column that is about to change underneath it.
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
      .Because("the index cannot be built while a row is still a string, so a rewrite that did not "
        + "finish has to fail at the next statement rather than leave a useless index");
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
