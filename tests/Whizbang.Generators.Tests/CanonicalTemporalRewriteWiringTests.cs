using TUnit.Assertions.Extensions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That the stored-form rewrite is derived at runtime and runs on the instance elected to migrate,
/// and that nothing about it is generated per property any more.
/// </summary>
/// <remarks>
/// <para>
/// The rewrite used to be emitted here as one statement per temporal property a generator had
/// discovered, which missed a member inherited from a base class, a nested object, an element of a
/// collection and the framework's own metadata. It is now derived at startup from the model Entity
/// Framework built and the serializer's metadata, the two things that read a document, so what a
/// reader reads is what the rewrite converts. The generated initializer only calls it.
/// </para>
/// <para>
/// It also ran on every replica, before the bootstrap, so the ledger and the function a
/// bootstrap-marked migration creates did not exist yet. It now runs after the election, on the
/// migrator or on an instance that could not be staged, never on one waiting for the migrator.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
public class CanonicalTemporalRewriteWiringTests {
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

  /// <summary>The initializer derives the rewrite from the model at runtime.</summary>
  [Test]
  public async Task TheRewriteIsDerivedFromTheModelAtRuntimeAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    await Assert.That(output).Contains(
      "Whizbang.Data.EFCore.Postgres.Perspectives.CanonicalTemporalRewrite.ForModel(", StringComparison.Ordinal)
      .Because("the model Entity Framework built and the serializer's metadata are what read a "
        + "document, so they are what the rewrite converts");
    await Assert.That(output).Contains(
      "Whizbang.Data.EFCore.Postgres.Perspectives.PerspectiveDocumentSerialization.Options", StringComparison.Ordinal)
      .Because("an opaque document's paths come from the options it is read with");
  }

  /// <summary>Nothing per property is generated for it any more.</summary>
  [Test]
  public async Task NothingIsGeneratedPerPropertyAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    await Assert.That(output).DoesNotContain("GetCanonicalTemporalRewrites", StringComparison.Ordinal);
    await Assert.That(output).DoesNotContain("jsonb_typeof(data -> 'OccurredAt')", StringComparison.Ordinal)
      .Because("a statement per discovered property is the partial discovery coming back");
    await Assert.That(output).DoesNotContain("-- @whizbang:commit-boundary", StringComparison.Ordinal)
      .Because("nothing may be applied on a second connection while the initializer's transaction "
        + "is open, so no boundary is emitted at all");
  }

  /// <summary>
  /// The rewrite runs after the election, and not on an instance waiting for the migrator.
  /// </summary>
  /// <remarks>
  /// Order in the generated text is order at runtime here: the election is awaited, its result is
  /// tested, and the rewrite follows. Before this the rewrite ran ahead of the bootstrap, when the
  /// ledger and the function it needs did not exist yet, on every replica at once.
  /// </remarks>
  [Test]
  public async Task TheRewriteRunsAfterTheElectionOnTheMigratorAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    var election = output.IndexOf("MigratorDutyStaging.ElectAsync(", StringComparison.Ordinal);
    var grant = output.IndexOf("await using var migratorGrant = staging.Grant;", StringComparison.Ordinal);
    var rewrite = output.IndexOf("CanonicalTemporalRewritePhase.ApplyAsync(", StringComparison.Ordinal);
    var wait = output.IndexOf("SchemaMigrationDeferral.DeferAsync(", StringComparison.Ordinal);

    await Assert.That(election).IsGreaterThan(-1);
    await Assert.That(rewrite).IsGreaterThan(grant)
      .Because("the rewrite needs the bootstrap's ledger and function, and one instance's duty");
    await Assert.That(rewrite).IsLessThan(wait)
      .Because("a waiter never rewrites; it waits for the migrator's result");
    await Assert.That(output).Contains(
      "staging.Stage != Whizbang.Data.Postgres.SchemaStage.Waiter", StringComparison.Ordinal);
  }
}
