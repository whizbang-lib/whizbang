// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The Dapper driver's share of the shared schema initializer: wait for the database, elect one migrator through the
/// shared election (#2561), migrate on that instance under the schema lock, and on every other instance wait for it;
/// then probe that the schema the stores need is there.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddWhizbangPostgres</c> only records this runner; nothing here runs until the host starts, so registration
/// never blocks on the network and the run has the container: the instance identity, the configuration, a logger.
/// </para>
/// <para>
/// The election is <see cref="MigratorElection"/>, the one the EF Core driver's generated initializer runs: the
/// bootstrap closure, the registration, the vote, then the wait for the migrator or the takeover of one that died. A
/// waiter whose migrator finished runs no pass and takes no schema lock; it converges and serves. Nothing about the
/// election is fatal: an instance that could not be staged migrates under the schema lock, as before.
/// </para>
/// </remarks>
/// <param name="connectionString">The database.</param>
/// <param name="options">The connection retry settings the wait uses.</param>
/// <param name="createInitializer">Builds the initializer for one attempt.</param>
/// <param name="logger">Where the wait reports its retries.</param>
/// <param name="services">Where the stage reporter, the elector and the instance identity are registered; null holds no
/// election.</param>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSchemaStartupTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMigratorElectionTests.cs</tests>
internal sealed class DapperSchemaInitializationRunner(
    string connectionString,
    PostgresOptions options,
    Func<PostgresSchemaInitializer> createInitializer,
    ILogger<DapperSchemaInitializationRunner> logger,
    IServiceProvider? services = null) : ISchemaInitializationRunner {

  /// <inheritdoc />
  public async Task RunAsync(CancellationToken cancellationToken) {
    // #1992: the host's one Assess verdict is this pass's input; a stand-down is refused before anything is applied.
    var assessment = await StartupVerdictBinding.ReadAsync(services, cancellationToken).ConfigureAwait(false);
    var retry = new PostgresConnectionRetry(options, logger);
    await retry.WaitForConnectionAsync(connectionString, cancellationToken).ConfigureAwait(false);
    if (StartupVerdictBinding.IsCurrent(assessment)) {
      // Current: no schema lock and no pass; this instance registers and the schema converges, as on every start.
      await SchemaInitializationStages.SchemaCurrentAsync(services, cancellationToken).ConfigureAwait(false);
      if (assessment!.OverriddenReason is { } overridden) {
        // #2022: started past an incompatibility verdict. It serves the schema as it is: it registers and records the
        // override on its row, and converges nothing.
        await createInitializer().ServeAsItIsAsync(overridden, cancellationToken).ConfigureAwait(false);
        return;
      }
      await _reportingConverge(createInitializer()).ConvergeCurrentAsync(cancellationToken).ConfigureAwait(false);
      return;
    }

    // #2553: W! first. A held application schema (stood down, or served as it is by its own override) is left as it is,
    // and the pass applies the W! migrations alone.
    var initializer = _reportingConverge(createInitializer());
    initializer.ApplicationHeld = assessment?.HoldsApplication == true;
    var (schema, bootstrap) = await initializer.ElectionClosureAsync(cancellationToken).ConfigureAwait(false);
    var commandTimeout = SchemaCommandTimeout.Resolve(connectionString);

    // #2561: the duty this instance holds from the election until this method returns, renewed by its keepalive while
    // the pass reports progress (each committed file), and released when this method returns, however it returns.
    await using var duty = MigratorElection.CreateDuty(services, commandTimeout, logger);
    using var progress = StartupProgress.ReportTo(duty.NoteProgress);
    var election = new MigratorElectionRequest(
      () => new NpgsqlConnection(connectionString), schema, SchemaInitializationLockKey.Compute(schema), bootstrap,
      commandTimeout, (_, ct) => initializer.IsCurrentAsync(ct), assessment?.OverriddenReason);
    await MigratorElection.StageAsync(duty, election, services, logger, cancellationToken).ConfigureAwait(false);

    // #2122: a rollback Assess decided on runs on the instance doing the schema work, before the pass, so the pass
    // migrates from this version's point of view. A waiter whose migrator finished has neither to run.
    var migratedElsewhere = await MigratorElection.RunPreLockAsync(
      duty, election,
      async ct => {
        if (assessment?.Rollback is { } rollback) {
          await createInitializer().RollBackSchemaAsync(rollback, ct).ConfigureAwait(false);
        }
      },
      services, logger, cancellationToken).ConfigureAwait(false);

    if (migratedElsewhere) {
      await SchemaInitializationStages.MigrationNotNeededAsync(services, "another instance migrated the schema", cancellationToken)
        .ConfigureAwait(false);
      await initializer.ConvergeMigratedAsync(cancellationToken).ConfigureAwait(false);
    } else {
      // The migrator's pass marks its connection's backend as the duty's, so one long statement does not lose the role.
      initializer.MigratorGrant = duty.Grant;
      await initializer.InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);
      if (assessment?.OverriddenReason is { } applicationAsItIs) {
        await initializer.RecordIncompatibilityOverrideAsync(applicationAsItIs, cancellationToken).ConfigureAwait(false);
      }
    }
    await retry.WaitForSchemaReadyAsync(connectionString, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The initializer, reporting the Converge stage just before its managed-object reconcile, as the EF Core driver does
  /// (#2563), so a host watching the stages sees the same sequence on either driver.
  /// </summary>
  private PostgresSchemaInitializer _reportingConverge(PostgresSchemaInitializer initializer) {
    initializer.Converging = ct => SchemaInitializationStages.ConvergingAsync(services, ct);
    return initializer;
  }
}
