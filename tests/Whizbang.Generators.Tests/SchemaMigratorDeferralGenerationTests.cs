// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That the generated initializer actually defers to the instance which won the schema lock.
/// </summary>
/// <remarks>
/// <para>
/// The deferral is unit tested on its own and integration tested against a real database, but
/// neither of those can tell whether the generated initializer calls it. Nothing is shipped whose
/// only proof of life is its own tests: a type with full cover and no caller is dead weight that
/// reads as a feature.
/// </para>
/// <para>
/// Asserted on the emitted text rather than by running it, because what is at issue is the wiring
/// the generator produces, and a behavioral test would pass just as well on a hand-written call
/// that the generator does not emit.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <docs>operations/infrastructure/migrations</docs>
public class SchemaMigratorDeferralGenerationTests {
  private const string MINIMAL_CONTEXT = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record Happened : IEvent;

    public record PlainModel {
      [StreamId]
      public Guid ThingId { get; init; }

      public string Label { get; init; } = string.Empty;
    }

    public class PlainPerspective : IPerspectiveFor<PlainModel, Happened> {
      public PlainModel Apply(PlainModel currentData, Happened eventData) => currentData;
    }

    [WhizbangDbContext]
    public class PlainDbContext : DbContext {
      public PlainDbContext(DbContextOptions<PlainDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _generatedAsync(string source) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    return string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
  }

  /// <summary>
  /// The initializer runs the shared migrator election (#2561): the bootstrap, the registration and the vote, then the
  /// wait or the takeover before the schema lock, and only then the pass.
  /// </summary>
  /// <remarks>
  /// The election itself (the waiter watching the migrator, anyone else the schema lock, a dead migrator taken over) is
  /// <c>MigratorElection</c>, shared with the Dapper driver and tested there on a real database. What the generated code
  /// owns is calling it, in this order, with the duty it holds for the whole initialization.
  /// </remarks>
  [Test]
  public async Task TheInitializerRunsTheSharedMigratorElectionAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    var stage = output.IndexOf(
      "Whizbang.Data.Postgres.MigratorElection.StageAsync(migratorDuty, election, serviceProvider, logger, cancellationToken)",
      StringComparison.Ordinal);
    var preLock = output.IndexOf("Whizbang.Data.Postgres.MigratorElection.RunPreLockAsync(", StringComparison.Ordinal);
    var pass = output.IndexOf("var retryAttempt = 0;", StringComparison.Ordinal);

    await Assert.That(stage).IsGreaterThan(0)
      .Because("winning the vote is the election; the losers have to be told to wait on it");
    await Assert.That(preLock).IsGreaterThan(stage);
    await Assert.That(pass).IsGreaterThan(preLock)
      .Because("a waiter waits for the migrator before the pass, whose fast path then finds nothing to apply");
  }

  /// <summary>The election is given the bootstrap closure and both questions a waiter asks.</summary>
  /// <remarks>
  /// Whether the schema is current says when to stop waiting; the duty (or, for anyone else, the schema lock) says
  /// whether there is anyone left to wait for. Given only the second, a migrator that committed reads as one that died.
  /// </remarks>
  [Test]
  public async Task TheElectionIsGivenTheBootstrapClosureAndTheSchemaCheckAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    await Assert.That(output).Contains("new Whizbang.Data.Postgres.MigratorElectionRequest(", StringComparison.Ordinal);
    await Assert.That(output).Contains("lockId, GetBootstrapScripts(), _schemaCommandTimeoutSeconds,", StringComparison.Ordinal)
      .Because("the bootstrap closure is applied first, so an election is possible on an empty database");
    await Assert.That(output).Contains("(c, ct) => _isSchemaCurrentAsync(c, ct), incompatibilityOverride);", StringComparison.Ordinal);
  }

  /// <summary>
  /// Every start checks the stored perspective registry against this build's (#2561), after the pass loop, beside the
  /// managed-object reconcile: no hash covers the registry, so a hash-clean restart, a Current schema or a waiter that
  /// skipped the pass's registries step would otherwise keep whatever registry it found. It is handed the payload the
  /// pass reconciles, and is skipped, with the rest of the converge, when the application's schema is held.
  /// </summary>
  [Test]
  public async Task EveryStartChecksThePerspectiveRegistryAgainstThisBuildAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    var held = output.IndexOf("if (incompatibilityOverride is not null || applicationHeld) {", StringComparison.Ordinal);
    var check = output.IndexOf("Whizbang.Data.Postgres.PerspectiveRegistryCurrency.ReconcileIfStaleAsync(", StringComparison.Ordinal);
    await Assert.That(held).IsGreaterThan(output.IndexOf("var retryAttempt = 0;", StringComparison.Ordinal))
      .Because("the converge follows the pass loop, which every start reaches");
    await Assert.That(check).IsGreaterThan(held).Because("a held application schema leaves its registry as it is");
    await Assert.That(output[check..]).Contains(
      "segmentConnectionFactory, \"", StringComparison.Ordinal);
    await Assert.That(output).Contains("_perspectiveRegistryPayloads(), _schemaCommandTimeoutSeconds, serviceProvider,", StringComparison.Ordinal);
    var accessor = output.IndexOf("_perspectiveRegistryPayloads() {", StringComparison.Ordinal);
    await Assert.That(output[accessor..]).Contains("new Whizbang.Core.Perspectives.PerspectiveRegistryPayload(", StringComparison.Ordinal);
    await Assert.That(output).DoesNotContain("__PERSPECTIVE_REGISTRY_JSON__", StringComparison.Ordinal)
      .Because("the accessor carries the registry JSON the generator embeds, as the pass's reconcile does");
  }

  /// <summary>
  /// The bootstrap subset is emitted, and is a subset.
  /// </summary>
  /// <remarks>
  /// Both halves matter. Emitting nothing would leave the election cycle in place and the only
  /// symptom would be a fleet quietly duplicating work. Emitting everything would apply all 151
  /// migrations before anything is elected, which is the entire job it is supposed to be deciding
  /// who does.
  /// </remarks>
  [Test]
  public async Task TheBootstrapSubsetIsEmittedAndIsASubsetAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    await Assert.That(output).Contains("record_capability", StringComparison.Ordinal)
      .Because("the function the elector calls has to be in the subset, or nothing can be elected");

    // The bootstrap list sits between its own declaration and the method that consumes it. Both
    // lists are emitted into the same file, so the window matters: counting over the whole output
    // would count all 151 migrations.
    var start = output.IndexOf("GetBootstrapMigrationScripts() {", StringComparison.Ordinal);
    await Assert.That(start).IsGreaterThan(0);
    var end = output.IndexOf("GetBootstrapScripts() {", StringComparison.Ordinal);
    await Assert.That(end).IsGreaterThan(start);

    var emitted = output[start..end].Split(".sql\"").Length - 1;
    await Assert.That(emitted).IsGreaterThan(0)
      .Because("emitting nothing leaves the election cycle in place, and the only symptom would be "
        + "a fleet quietly duplicating work");
    // Measured against the whole set rather than a fixed count: the bootstrap grows deliberately (each addition is
    // pinned by MigrationBootstrapRegionsTests) as Preflight takes on the instance tables (#1636), and what must never
    // happen is that it stops being a small subset of the migrations.
    var all = output.Split(".sql\"").Length - 1 - emitted;
    await Assert.That(emitted * 10).IsLessThan(all)
      .Because("the bootstrap is a small subset, under a tenth of the migrations; if it grows toward the whole "
        + "set it has stopped being a bootstrap and is doing the job it was meant to be electing someone for");
  }

  /// <summary>The full list carries every migration the generator embeds, each exactly once.</summary>
  /// <remarks>
  /// The migrations are the generator's own embedded resources, so this is what makes an empty or
  /// truncated list impossible to ship rather than something the generator guards at run time.
  /// </remarks>
  [Test]
  public async Task TheFullListEmitsEveryEmbeddedMigrationAsync() {
    var assembly = typeof(Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator).Assembly;
    var prefix = $"{assembly.GetName().Name}.Templates.Migrations.";
    var embedded = assembly.GetManifestResourceNames()
      .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
      .Select(n => n[prefix.Length..])
      .ToList();

    var output = await _generatedAsync(MINIMAL_CONTEXT);
    var start = output.IndexOf("GetMigrationScripts() {", StringComparison.Ordinal);
    await Assert.That(start).IsGreaterThan(0);
    var end = output.IndexOf("GetBootstrapMigrationScripts() {", StringComparison.Ordinal);
    await Assert.That(end).IsGreaterThan(start);
    var window = output[start..end];

    await Assert.That(embedded.Count).IsGreaterThan(100)
      .Because("the generator embeds the whole migration set");
    await Assert.That(window.Split(".sql\"").Length - 1).IsEqualTo(embedded.Count);
    foreach (var name in embedded) {
      await Assert.That(window).Contains($"(\"{name}\", global::System.Text.Encoding.UTF8.GetString(@\"", StringComparison.Ordinal);
    }
  }

  /// <summary>
  /// The migrator keeps its assignment through the migration: its keepalive renews it from the election until the
  /// initializer returns (#2127), it verifies before each attempt, and it marks the DDL connection's backend, so one
  /// long statement does not lose the role (#968, decision 1).
  /// </summary>
  [Test]
  public async Task TheMigratorRenewsItsRole_AndMarksTheBackendRunningTheMigrationAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    await Assert.That(output).Contains(
      "await using var migratorDuty = Whizbang.Data.Postgres.MigratorElection.CreateDuty(", StringComparison.Ordinal)
      .Because("the duty, its keepalive and its stall limit (#2165) are the shared election's, held for the whole method");
    await Assert.That(output).Contains("Whizbang.Data.Postgres.StartupProgress.ReportTo(migratorDuty.NoteProgress)", StringComparison.Ordinal);
    await Assert.That(output).Contains("migratorDuty.Grant.VerifyStillHeldAsync", StringComparison.Ordinal);
    await Assert.That(output).Contains("PgRoleElector.MarkDutyBackendAsync", StringComparison.Ordinal);
  }

  /// <summary>
  /// A deferral that ended because the migrator finished skips the DDL entirely.
  /// </summary>
  /// <remarks>
  /// The point of waiting. Falling through to take the lock anyway would reintroduce the per-replica
  /// transaction and in-lock re-check the deferral exists to remove, and the suite would still be
  /// green because the outcome is identical. Each step's wait is the step runner's (its own tests pin
  /// it); what the generated pass must do is stop when the runner says another instance finished.
  /// </remarks>
  [Test]
  public async Task AFinishedMigratorEndsTheInitializationWithoutTakingTheLockAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    await Assert.That(output).Contains("Whizbang.Data.EFCore.Postgres.SchemaStepRunner(", StringComparison.Ordinal);
    await Assert.That(output).Contains("ct => _isSchemaCurrentAsync(connection, ct)", StringComparison.Ordinal)
      .Because("a step that loses the lock waits on the same three-part currency check the fast path uses");
    await Assert.That(output).Contains("if (steps.CompletedElsewhere)", StringComparison.Ordinal);
    await Assert.That(output).Contains("if (pass is null)", StringComparison.Ordinal);
  }
}
