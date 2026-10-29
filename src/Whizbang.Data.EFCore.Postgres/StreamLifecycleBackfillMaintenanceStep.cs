// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The maintenance step that seeds the stream lifecycle ledger for the streams the event store held before the ledger
/// existed (#1732); see <see cref="StreamLifecycleBackfill"/>.
/// </summary>
/// <remarks>
/// Idle once the backfill is recorded as done, which a new database is from its first start: one keyed read per cycle.
/// Until then each cycle runs a bounded number of bounded batches, each its own transaction, and an instance that finds
/// another walking the cursor steps aside for the cycle.
/// </remarks>
/// <param name="dbContextType">The DbContext whose schema the ledger lives in.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="batchSize">The most streams one batch visits.</param>
/// <param name="maxRoundsPerRun">The most batches one cycle runs.</param>
/// <docs>fundamentals/events/stream-end-of-life#the-one-time-backfill</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleBackfillMaintenanceStepTests.cs</tests>
public sealed class StreamLifecycleBackfillMaintenanceStep(
    Type dbContextType,
    ILogger<StreamLifecycleBackfillMaintenanceStep>? logger = null,
    int batchSize = StreamLifecycleBackfill.DEFAULT_BATCH_SIZE,
    int maxRoundsPerRun = StreamLifecycleBackfill.DEFAULT_MAX_ROUNDS) : IMaintenanceStep {

  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));

  /// <inheritdoc />
  public string Name => "stream-lifecycle-backfill";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var context = (DbContext)services.GetRequiredService(_dbContextType);
    // The schema the work coordinator resolves for the same context, with its fallback for a model mapped without one.
    var quotedSchema = PgIdentifier.Quote(EFCoreWorkCoordinator<DbContext>.ResolveSchema(context.Model, typeof(OutboxRecord), null));

    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    try {
      var connection = (NpgsqlConnection)context.Database.GetDbConnection();
      await StreamLifecycleBackfill.RunAsync(
        connection, quotedSchema, batchSize, maxRoundsPerRun, logger, cancellationToken).ConfigureAwait(false);
    } finally {
      await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
  }
}
