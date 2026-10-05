// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The Dapper driver's maintenance step that completes perspective field moves: the same follow-up the EF Core
/// driver runs (issue #1010).
/// </summary>
/// <remarks>
/// <para>
/// The start that promotes a field arms its column before the table swap adds it (see
/// <c>PerspectiveSchemaGenerator</c>), and fills it from the document. An instance still on the previous
/// release keeps writing during the rolling deploy without knowing the column, so its rows carry the value in
/// the document and null in the column, and a filter, sort or count on the column misses or misplaces them
/// until their next event. This step fills them, settles demotions and reports columns only a rebuild can
/// restore; see <see cref="PhysicalColumnFill"/>.
/// </para>
/// <para>
/// Idle unless something is armed. Otherwise one instance per claim window runs it: the one that takes the
/// window's claim in <c>wh_unique_emission_claims</c>, the claim <c>PublishOnceAsync</c> takes, through the
/// registered <see cref="IClaimedEmissionStore"/> when there is one and directly otherwise. Every batch is
/// bounded, and only ever fills a column where it is null, so a second instance running it would change
/// nothing a first had not.
/// </para>
/// </remarks>
/// <param name="connectionString">The database the perspectives are in.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="timeProvider">The clock the claim window is read from.</param>
/// <param name="batchSize">The most rows one batch fills in one column.</param>
/// <param name="maxRoundsPerRun">The most batches one run fills per column.</param>
/// <param name="settle">How long a move stays watched after the start that made it.</param>
/// <docs>fundamentals/perspectives/physical-fields#rows-written-during-a-rolling-deploy</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperPhysicalFieldMoveTests.cs</tests>
public sealed class DapperPhysicalColumnFillMaintenanceStep(
    string connectionString,
    ILogger<DapperPhysicalColumnFillMaintenanceStep>? logger = null,
    TimeProvider? timeProvider = null,
    int batchSize = PhysicalColumnFill.DEFAULT_BATCH_SIZE,
    int maxRoundsPerRun = PhysicalColumnFill.DEFAULT_MAX_ROUNDS,
    TimeSpan? settle = null) : IMaintenanceStep {

  // The Dapper driver keeps its tables in the connection's default schema.
  private const string SCHEMA = "public";
  private const string QUOTED_SCHEMA = "\"public\"";

  private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
    ? throw new ArgumentException("A connection string is required.", nameof(connectionString))
    : connectionString;
  private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
  private readonly TimeSpan _settle = settle ?? PhysicalColumnFill.DefaultSettle;

  /// <inheritdoc />
  public string Name => "physical-column-fill";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    if (!await PhysicalColumnFill.AnyArmedAsync(connection, QUOTED_SCHEMA, cancellationToken).ConfigureAwait(false)) {
      return;
    }

    var key = PhysicalColumnFill.ClaimKey(SCHEMA, _timeProvider.GetUtcNow());
    var claimed = services.GetService<IClaimedEmissionStore>() is { } claims
      ? await claims.TryClaimAsync(key, Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false)
      : await PhysicalColumnFill.TryClaimAsync(connection, QUOTED_SCHEMA, key, cancellationToken).ConfigureAwait(false);
    if (!claimed) {
      return;
    }

    await PhysicalColumnFill.RunAsync(
      connection, QUOTED_SCHEMA, batchSize, maxRoundsPerRun, _settle, _logger, cancellationToken).ConfigureAwait(false);
  }
}
