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

  /// <summary>An instance that loses the lock waits on the winner instead of contending again.</summary>
  [Test]
  public async Task TheInitializerDefersToTheInstanceHoldingTheLockAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    await Assert.That(output).Contains("SchemaMigrationDeferral.DeferAsync", StringComparison.Ordinal)
      .Because("winning the try-lock is the election; the losers have to be told to wait on it");
  }

  /// <summary>The wait is fed both questions, not just the schema's state.</summary>
  /// <remarks>
  /// Whether the schema is current says when to stop waiting; whether anything still holds the lock
  /// says whether there is anyone left to wait for. Given only the first, an instance whose
  /// migrator died would wait for ever on a schema that will never advance.
  /// </remarks>
  [Test]
  public async Task TheDeferralIsGivenBothTheSchemaAndTheLockProbeAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    await Assert.That(output).Contains("_isSchemaCurrentAsync", StringComparison.Ordinal);
    await Assert.That(output).Contains("AdvisoryLockProbe.IsHeldElsewhereAsync", StringComparison.Ordinal);
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
    await Assert.That(emitted).IsLessThan(10)
      .Because("the bootstrap is a handful of files; if it grows to the whole set it has stopped "
        + "being a bootstrap and is doing the job it was meant to be electing someone for");
  }

  /// <summary>The bootstrap runs before anything is elected.</summary>
  /// <remarks>
  /// Order is the whole point: the capability function the election needs is created by the
  /// bootstrap, so electing first is refused for a reason that has nothing to do with contention.
  /// </remarks>
  [Test]
  public async Task TheBootstrapRunsBeforeTheElectionAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    var bootstrap = output.IndexOf("SchemaBootstrapPhase.ApplyAsync", StringComparison.Ordinal);
    var elect = output.IndexOf("MigratorDutyStaging.ElectAsync", StringComparison.Ordinal);

    await Assert.That(bootstrap).IsGreaterThan(0);
    await Assert.That(elect).IsGreaterThan(0);
    await Assert.That(bootstrap).IsLessThan(elect);
  }

  /// <summary>An instance registers before it contends for the duty.</summary>
  [Test]
  public async Task TheInstanceRegistersBeforeContendingAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    await Assert.That(output).Contains("SchemaBootstrapPhase.RegisterInstanceAsync",
      StringComparison.Ordinal)
      .Because("record_capability refuses an instance that is not in the registry, so an election "
        + "without a registration is refused for the wrong reason");
  }

  /// <summary>A non-holder waits on the duty lock, not the schema lock.</summary>
  /// <remarks>
  /// The two keys are different. Watching the schema lock would report the migrator as gone the
  /// moment it finished its bootstrap and before it started migrating.
  /// </remarks>
  [Test]
  public async Task AWaiterWatchesTheDutyLockAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    await Assert.That(output).Contains("DutyLockKey.Compute", StringComparison.Ordinal);
    await Assert.That(output).Contains("StartupDuties.MIGRATOR", StringComparison.Ordinal);
  }

  /// <summary>
  /// A deferral that ended because the migrator finished skips the DDL entirely.
  /// </summary>
  /// <remarks>
  /// The point of waiting. Falling through to take the lock anyway would reintroduce the per-replica
  /// transaction and in-lock re-check the deferral exists to remove, and the suite would still be
  /// green because the outcome is identical.
  /// </remarks>
  [Test]
  public async Task AFinishedMigratorEndsTheInitializationWithoutTakingTheLockAsync() {
    var output = await _generatedAsync(MINIMAL_CONTEXT);

    await Assert.That(output).Contains("anotherInstanceMigrated = true", StringComparison.Ordinal);
    await Assert.That(output).Contains("if (anotherInstanceMigrated)", StringComparison.Ordinal);
  }
}
