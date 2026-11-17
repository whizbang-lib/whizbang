// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The Dapper driver's share of the shared schema initializer: wait for the database, migrate under the schema lock
/// (registering this instance), then probe that the schema the stores need is there.
/// </summary>
/// <remarks>
/// <c>AddWhizbangPostgres</c> only records this runner; nothing here runs until the host starts, so registration
/// never blocks on the network and the run has the container: the instance identity, the configuration, a logger.
/// </remarks>
/// <param name="connectionString">The database.</param>
/// <param name="options">The connection retry settings the wait uses.</param>
/// <param name="createInitializer">Builds the initializer for one attempt.</param>
/// <param name="logger">Where the wait reports its retries.</param>
/// <param name="services">Where the stage reporter is registered; null reports no stage.</param>
/// <docs>data/turnkey-initialization</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSchemaStartupTests.cs</tests>
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
      await createInitializer().ConvergeCurrentAsync(cancellationToken).ConfigureAwait(false);
      return;
    }
    // #1636: this driver holds no election, so Preflight ends here, once the database answers, and Elect is reported
    // skipped; what waits for the end of Preflight (the notification connection and its alive-lock) starts now.
    await SchemaInitializationStages.ElectedAsync(
      services, new SchemaStaging(SchemaStage.Unstaged, null, "the Dapper driver holds no election"), cancellationToken)
      .ConfigureAwait(false);
    // #2122: a rollback Assess decided on runs first, so the pass migrates from this version's point of view.
    if (assessment?.Rollback is { } rollback) {
      await createInitializer().RollBackSchemaAsync(rollback, cancellationToken).ConfigureAwait(false);
    }
    await createInitializer().InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);
    await retry.WaitForSchemaReadyAsync(connectionString, cancellationToken).ConfigureAwait(false);
  }
}
