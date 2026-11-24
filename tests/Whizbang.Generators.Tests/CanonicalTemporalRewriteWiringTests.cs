// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  /// The rewrite runs after the election, on the instance doing the schema work, and before the DDL.
  /// </summary>
  /// <remarks>
  /// Order in the generated text is order at runtime here. The election (the bootstrap, the vote) is the shared
  /// <c>MigratorElection.StageAsync</c> (#2561); the phases before the schema lock are its <c>RunPreLockAsync</c>, which
  /// runs the work it is given on the migrator, on an instance that could not be staged, and on a waiter that took over
  /// from a migrator that died, and never on a waiter whose migrator finished. The rewrite is that work, so it follows
  /// the election, precedes the transaction that indexes its result, and has one call site.
  /// </remarks>
  [Test]
  public async Task TheRewriteRunsAfterTheElectionOnTheMigratorAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    var election = output.IndexOf("Whizbang.Data.Postgres.MigratorElection.StageAsync(", StringComparison.Ordinal);
    var preLock = output.IndexOf("Whizbang.Data.Postgres.MigratorElection.RunPreLockAsync(", StringComparison.Ordinal);
    var call = output.IndexOf("rewriteStoredFormsAsync(segmentConnectionFactory!);", StringComparison.Ordinal);
    var ddl = output.IndexOf("var retryAttempt = 0;", StringComparison.Ordinal);

    await Assert.That(election).IsGreaterThan(-1);
    await Assert.That(preLock).IsGreaterThan(election)
      .Because("the rewrite needs the bootstrap's ledger and function, and one instance's duty");
    await Assert.That(call).IsGreaterThan(preLock).And.IsLessThan(ddl)
      .Because("the rewrite is the work the shared pre-lock phase runs on whoever does the schema work (#2127), "
        + "committed before the transaction that indexes its result");
    await Assert.That(call)
      .IsEqualTo(output.LastIndexOf("rewriteStoredFormsAsync(segmentConnectionFactory", StringComparison.Ordinal))
      .Because("one call site: the migrator, an instance that could not be staged, and a waiter taking over all reach it");
    await Assert.That(output.IndexOf("CanonicalTemporalRewritePhase.ApplyAsync(", StringComparison.Ordinal))
      .IsEqualTo(output.LastIndexOf("CanonicalTemporalRewritePhase.ApplyAsync(", StringComparison.Ordinal))
      .Because("every path shares one body, so a change to one cannot drift from the other");
  }

  /// <summary>
  /// The waits and the takeover are the shared election's, never a copy in the generated initializer (#2561).
  /// </summary>
  /// <remarks>
  /// A waiter watches the migrator's duty, anyone else the schema lock, and a waiter whose migrator died votes before it
  /// works: that logic lives in <c>MigratorElection</c>, which the Dapper driver runs too, and its behavior is tested on
  /// a real database there and through this initializer. A copy here would be free to drift from it.
  /// </remarks>
  [Test]
  public async Task TheWaitAndTheTakeoverAreTheSharedElectionsAsync() {
    var output = await _generatedAsync(TEMPORAL_MODEL);

    foreach (var copy in new[] {
        "MigratorPreLockPhase.RunAsync(", "SchemaMigrationDeferral.DeferAsync(", "MigratorWatch.IsMigratingElsewhereAsync(",
        "MigratorDutyStaging.ElectAsync(", "SchemaBootstrapPhase.ApplyAsync(" }) {
      await Assert.That(output).DoesNotContain(copy, StringComparison.Ordinal)
        .Because($"{copy} is called by the shared election, not by generated code");
    }
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
