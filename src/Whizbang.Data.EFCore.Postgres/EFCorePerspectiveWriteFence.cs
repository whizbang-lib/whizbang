// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The perspective write fence (#2556) as the Entity Framework driver applies it, to its per-pair upsert and its
/// set-based sink alike: the tenure ledger and the coordinator metrics come from the host the context was built from,
/// the check runs on the context's connection and transaction, and a fenced stream is counted
/// (<c>whizbang.work_coordinator.fenced_writes</c>, kind <c>perspective_write</c>) and logged at debug.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWritesAreFencedTests.cs</tests>
internal static partial class EFCorePerspectiveWriteFence {
  internal const string KIND = "perspective_write";
  private const string LOGGER_CATEGORY = "Whizbang.Data.EFCore.Postgres.EFCorePerspectiveWriteFence";

  /// <summary>The fenced streams among <paramref name="streams"/>; empty, with no round trip, off map mode.</summary>
  internal static async Task<IReadOnlySet<Guid>> StaleAsync(DbContext context, IEnumerable<Guid> streams, CancellationToken cancellationToken) {
    var host = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.ApplicationServiceProvider;
    if (host?.GetService<PartitionTenures>() is not { } tenures || context.Database.GetDbConnection() is not NpgsqlConnection connection) {
      return new HashSet<Guid>();
    }
    var openedHere = connection.State != System.Data.ConnectionState.Open;
    if (openedHere) {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
    IReadOnlySet<Guid> stale;
    try {
      stale = await PerspectiveWriteFence.StaleAsync(
        connection,
        context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction,
        PgIdentifier.QualifyPrefix(EFCoreWorkCoordinator<DbContext>.ResolveSchema(context.Model, typeof(OutboxRecord), logger: null)),
        tenures, streams, cancellationToken).ConfigureAwait(false);
    } finally {
      if (openedHere) {
        await connection.CloseAsync().ConfigureAwait(false);
      }
    }
    if (stale.Count > 0) {
      host.GetService<WorkCoordinatorMetrics>()?.FencedWrites.Add(
        stale.Count, new KeyValuePair<string, object?>(WorkCoordinatorMetrics.KIND_TAG, KIND));
      var logger = host.GetService<ILoggerFactory>()?.CreateLogger(LOGGER_CATEGORY);
      if (logger is not null) {
        LogFenced(logger, stale.Count);
      }
    }
    return stale;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
    Message = "Fenced {Count} perspective write(s): the stream's partition is no longer owned at the epoch its work was claimed under, so the current owner applies it")]
  private static partial void LogFenced(ILogger logger, int count);
}
