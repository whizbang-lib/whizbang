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

  /// <summary>
  /// A table this release creates is recorded in the microsecond form, settled, at creation.
  /// </summary>
  /// <remarks>
  /// Recorded before the CREATE TABLE and only when the table does not exist yet, which is the only
  /// moment "fresh" can be told from "upgraded": a table an older release created has rows in the
  /// mixed-unit form and no ledger row, and recording it as converted here would make the rewrite
  /// skip it forever. The rewrite records an upgraded table itself, after converting it.
  /// </remarks>
  [Test]
  public async Task AFreshTableIsRecordedAtTheMicrosecondFormAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    // The schema SQL is embedded in a verbatim C# string, so a double quote in it reads doubled here,
    // and the schema is the one derived from the namespace.
    var insert = output.IndexOf(
      "INSERT INTO \"\"testapp\"\".wh_perspective_forms (table_name, temporal_form, applied_at, settled_at)",
      StringComparison.Ordinal);
    var create = output.IndexOf("CREATE TABLE IF NOT EXISTS \"\"testapp\"\".wh_per_report (", StringComparison.Ordinal);

    await Assert.That(insert).IsGreaterThan(-1);
    await Assert.That(create).IsGreaterThan(insert)
      .Because("only before the CREATE TABLE can a fresh table be told from one an older release made");
    await Assert.That(output).Contains(
      "VALUES ('wh_per_report', 2, now(), now())", StringComparison.Ordinal);
    await Assert.That(output).Contains(
      "IF to_regclass('\"\"testapp\"\".wh_per_report') IS NULL AND to_regclass('\"\"testapp\"\".wh_perspective_forms') IS NOT NULL THEN",
      StringComparison.Ordinal)
      .Because("an upgraded table is left to the rewrite, and a database without the ledger yet is left "
        + "alone; inside a DO block, so an absent ledger is a branch not taken rather than a statement "
        + "that fails to plan");
    await Assert.That(output).Contains("ON CONFLICT (table_name) DO NOTHING", StringComparison.Ordinal);
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

  /// <summary>
  /// A waiter that takes over from a migrator that died runs the rewrite before it contends for
  /// the DDL lock, through the same body the migrator runs.
  /// </summary>
  /// <remarks>
  /// A migrator killed mid-rewrite leaves the remaining tables unconverted, and every replacement
  /// instance is a waiter. Their wait ends with the holder gone and they run the DDL loop, but the
  /// rewrite used to sit before the wait behind a "not a waiter" guard, so nothing converted the
  /// rest until a later start happened to elect a migrator.
  /// </remarks>
  [Test]
  public async Task AWaiterThatTakesOverRunsTheRewriteBeforeTheDdlAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    var wait = output.IndexOf("SchemaMigrationDeferral.DeferAsync(", StringComparison.Ordinal);
    var takeover = output.IndexOf("Whizbang.Data.Postgres.SchemaDeferralOutcome.MigratingInstanceGone", wait, StringComparison.Ordinal);
    var ddl = output.IndexOf("var retryAttempt = 0;", StringComparison.Ordinal);
    var rewrite = output.IndexOf("await rewriteStoredFormsAsync(", StringComparison.Ordinal);

    await Assert.That(wait).IsGreaterThan(-1);
    await Assert.That(takeover).IsGreaterThan(wait)
      .Because("the takeover is decided by how the wait ended");
    await Assert.That(rewrite).IsGreaterThan(takeover).And.IsLessThan(ddl)
      .Because("whoever does the schema work rewrites after the wait has decided that it does, and "
        + "before the transaction that indexes the result");
    await Assert.That(rewrite)
      .IsEqualTo(output.LastIndexOf("await rewriteStoredFormsAsync(", StringComparison.Ordinal))
      .Because("one call site: the migrator, an instance that could not be staged, and a waiter taking "
        + "over all reach the rewrite through it");
    await Assert.That(output).Contains(
      "doesTheWork = !isWaiter || waitOutcome == Whizbang.Data.Postgres.SchemaDeferralOutcome.MigratingInstanceGone;",
      StringComparison.Ordinal)
      .Because("a waiter rewrites only when it takes the work over; any other instance rewrites once its wait ends");
    await Assert.That(output.IndexOf("CanonicalTemporalRewritePhase.ApplyAsync(", StringComparison.Ordinal))
      .IsEqualTo(output.LastIndexOf("CanonicalTemporalRewritePhase.ApplyAsync(", StringComparison.Ordinal))
      .Because("both paths share one body, so a change to one cannot drift from the other");
  }

  /// <summary>
  /// An instance that would rewrite while another session holds the schema lock watches that lock
  /// instead, and rewrites when the wait ends.
  /// </summary>
  /// <remarks>
  /// The rewrite phase waits for the schema lock rather than skipping, for up to its whole command
  /// budget, because the holder may be a sibling's bootstrap that converts nothing. That budget is
  /// ten minutes. An instance that could not be staged and started while a migrator held the lock
  /// for a long migration therefore sat inside the rewrite for the length of it, logging nothing
  /// about deferring, and never reached the deferral that watches the lock and reports it. The
  /// probe below sends such an instance into the same wait a waiter uses, on the schema lock, and
  /// the rewrite follows the wait: a settled no-op under a schema someone else brought up to date,
  /// the real thing over a schema whose holder released it still behind.
  /// </remarks>
  [Test]
  public async Task AnInstanceThatWouldRewriteBehindAHeldSchemaLockWaitsOnTheLockFirstAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    var probe = output.IndexOf(
      "Whizbang.Data.Postgres.AdvisoryLockProbe.IsHeldElsewhereAsync(waitConnection, lockId, cancellationToken)",
      StringComparison.Ordinal);
    var wait = output.IndexOf("SchemaMigrationDeferral.DeferAsync(", StringComparison.Ordinal);
    var rewrite = output.IndexOf("await rewriteStoredFormsAsync(", StringComparison.Ordinal);

    await Assert.That(probe).IsGreaterThan(-1)
      .Because("a schema lock held by another session is what sends an instance that would rewrite into the wait");
    await Assert.That(probe).IsLessThan(wait)
      .Because("the probe decides whether there is anything to wait for");
    await Assert.That(wait).IsLessThan(rewrite)
      .Because("the rewrite follows the wait rather than sitting inside its own for the whole budget");
    await Assert.That(output).Contains("var watchedKey = isWaiter", StringComparison.Ordinal)
      .Because("a waiter watches the duty lock and everyone else watches the schema lock, through one wait");
  }

  /// <summary>
  /// The migrator names the other releases alive in the fleet before it rewrites, at Warning.
  /// </summary>
  /// <remarks>
  /// A rewrite that changes a stored unit is not safe under a mixed fleet, and the migrator cannot
  /// refuse to run under a rolling update without deadlocking the rollout. So it says what it saw,
  /// before it does anything, from the registry every instance heartbeats into.
  /// </remarks>
  [Test]
  public async Task TheMigratorWarnsAboutOtherReleasesBeforeRewritingAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    var warning = output.IndexOf("Whizbang.Data.Postgres.FleetVersions.OtherLiveVersionsAsync(", StringComparison.Ordinal);
    var rewrite = output.IndexOf("CanonicalTemporalRewritePhase.ApplyAsync(", StringComparison.Ordinal);

    await Assert.That(warning).IsGreaterThan(-1);
    await Assert.That(warning).IsLessThan(rewrite)
      .Because("the warning is worth nothing after the rows are already written");
    await Assert.That(output).Contains("Other releases are alive in the fleet for schema {Schema}", StringComparison.Ordinal);
    await Assert.That(output).Contains("ILibraryVersionProvider", StringComparison.Ordinal)
      .Because("this instance's release is what the others are compared against");
  }
}
