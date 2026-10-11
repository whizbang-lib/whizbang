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
/// <docs>data/turnkey-initialization</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSchemaStartupTests.cs</tests>
internal sealed class DapperSchemaInitializationRunner(
    string connectionString,
    PostgresOptions options,
    Func<PostgresSchemaInitializer> createInitializer,
    ILogger<DapperSchemaInitializationRunner> logger) : ISchemaInitializationRunner {

  /// <inheritdoc />
  public async Task RunAsync(CancellationToken cancellationToken) {
    var retry = new PostgresConnectionRetry(options, logger);
    await retry.WaitForConnectionAsync(connectionString, cancellationToken).ConfigureAwait(false);
    await createInitializer().InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);
    await retry.WaitForSchemaReadyAsync(connectionString, cancellationToken).ConfigureAwait(false);
  }
}
