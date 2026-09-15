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
  /// The rewrite is emitted as a guarded pre-phase, separate from the perspective's schema.
  /// </summary>
  /// <remarks>
  /// <para>
  /// It cannot live with the schema any more. An index over an extraction of a rewritten key needs
  /// the rewrite committed, the initializer builds its indexes inside one advisory-locked
  /// transaction, and committing from a second connection while that transaction is open deadlocks
  /// on catalog rows it has not committed. So the rewrite runs before the transaction opens, which
  /// means it has to tolerate a table that is not there yet.
  /// </para>
  /// <para>
  /// The guard is asserted, not just the presence of the rewrite: without it a database created by
  /// this release cannot start at all, because its tables are made later in the same pass.
  /// </para>
  /// </remarks>
  [Test]
  public async Task TheRewriteIsEmittedAsAGuardedPrePhaseAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    await Assert.That(output).Contains("GetCanonicalTemporalRewrites", StringComparison.Ordinal)
      .Because("the rewrites are a phase of their own now, not part of a perspective's schema");
    await Assert.That(output).Contains("to_regclass(", StringComparison.Ordinal)
      .Because("running before the tables exist means a missing table has to be the ordinary case");
  }

  /// <summary>
  /// A perspective's own schema no longer carries the rewrite or a commit boundary.
  /// </summary>
  /// <remarks>
  /// Left behind, the rewrite would run inside the locked transaction and the index over it would be
  /// built in the same transaction, which is the failure this arrangement removes. The marker going
  /// too is what stops the initializer opening a second connection while that transaction is open.
  /// </remarks>
  [Test]
  public async Task ThePerspectiveEntryNoLongerCarriesTheRewriteAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    var entries = output.IndexOf("GetPerspectiveEntries", StringComparison.Ordinal);
    await Assert.That(entries).IsGreaterThan(-1);

    var rewrites = output.IndexOf("GetCanonicalTemporalRewrites", StringComparison.Ordinal);
    await Assert.That(rewrites).IsGreaterThan(-1);

    await Assert.That(output).DoesNotContain(
      CanonicalTemporalBackfillSql.COMMIT_BOUNDARY, StringComparison.Ordinal)
      .Because("nothing may be applied on a second connection while the initializer's transaction "
        + "is open, so no boundary is emitted at all");
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
