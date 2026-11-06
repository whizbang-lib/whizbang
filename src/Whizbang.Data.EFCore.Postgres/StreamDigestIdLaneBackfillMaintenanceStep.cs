// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The maintenance step that fills the id lanes of stream digests stored before migration 336 (#2106); see
/// <see cref="StreamDigestIdLaneBackfill"/>.
/// </summary>
/// <remarks>
/// Idle once the backfill is recorded as done, which a new database is from its first start: one keyed read per cycle.
/// Until then each cycle runs a bounded number of bounded batches, each its own transaction, and an instance that finds
/// another walking the cursor steps aside for the cycle. Drift a batch finds is logged at Warning and counted.
/// </remarks>
/// <param name="dbContextType">The DbContext whose schema the digests live in.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="batchSize">The most streams one batch visits.</param>
/// <param name="maxRoundsPerRun">The most batches one cycle runs.</param>
/// <docs>resilience/stream-integrity#one-digest-fold</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestIdLaneBackfillSqlTests.cs</tests>
public sealed class StreamDigestIdLaneBackfillMaintenanceStep(
    Type dbContextType,
    ILogger<StreamDigestIdLaneBackfillMaintenanceStep>? logger = null,
    int batchSize = StreamDigestIdLaneBackfill.DEFAULT_BATCH_SIZE,
    int maxRoundsPerRun = StreamDigestIdLaneBackfill.DEFAULT_MAX_ROUNDS) : IMaintenanceStep {

  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));

  /// <inheritdoc />
  public string Name => "stream-digest-id-lane-backfill";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var context = (DbContext)services.GetRequiredService(_dbContextType);
    var quotedSchema = PgIdentifier.Quote(EFCoreWorkCoordinator<DbContext>.ResolveSchema(context.Model, typeof(OutboxRecord), null));
    var metrics = services.GetService<StreamIntegrityMetrics>();

    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    try {
      var connection = (NpgsqlConnection)context.Database.GetDbConnection();
      await StreamDigestIdLaneBackfill.RunAsync(
        connection, quotedSchema, batchSize, maxRoundsPerRun, logger, metrics, cancellationToken).ConfigureAwait(false);
    } finally {
      await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
  }
}
