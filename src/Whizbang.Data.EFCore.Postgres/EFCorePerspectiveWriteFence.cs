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
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The perspective write fence (#2556) as the Entity Framework driver applies it, to its set-based sink and its per-pair
/// upsert alike. The tenure ledger and the coordinator metrics come from the host the context was built from. The fence
/// is carried in the statement that writes each row (<see cref="PerspectiveWriteFence"/>), and a fenced row is counted
/// (<c>whizbang.work_coordinator.fenced_writes</c>, kind <c>perspective_write</c>) and logged at debug.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWritesAreFencedTests.cs</tests>
internal static partial class EFCorePerspectiveWriteFence {
  internal const string KIND = "perspective_write";
  private const string LOGGER_CATEGORY = "Whizbang.Data.EFCore.Postgres.EFCorePerspectiveWriteFence";

  /// <summary>The fence for writes on <paramref name="context"/>, or null when its host keeps no tenure ledger.</summary>
  internal static PerspectiveWriteFence? Of(DbContext context) =>
    _host(context)?.GetService<PartitionTenures>() is { } tenures
      ? PerspectiveWriteFence.For(
        PgIdentifier.QualifyPrefix(EFCoreWorkCoordinator<DbContext>.ResolveSchema(context.Model, typeof(OutboxRecord), logger: null)), tenures)
      : null;

  /// <summary>
  /// Holds the stream's partition at the epoch its work in flight was claimed under, in the context's transaction, for
  /// the write paths that cannot carry the fence in their own statement (the Entity Framework change-tracker write). In
  /// an ambient transaction the hold lasts until it commits, as the statement's would; nothing when the stream has no
  /// work in flight.
  /// </summary>
  /// <exception cref="PartitionOwnershipLostException">The partition's epoch moved on.</exception>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "The schema prefix cannot be a bind parameter, so it is interpolated; it is built by PgIdentifier, which doubles any embedded quote. Every value is a bound parameter.")]
  internal static async Task HoldAsync(DbContext context, Guid streamId, CancellationToken cancellationToken) {
    if (Of(context) is not { } fence || fence.TenureOf(streamId) is not { } tenure) {
      return;
    }
    var connection = (NpgsqlConnection)context.Database.GetDbConnection();
    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    try {
      await using var cmd = connection.CreateCommand();
      cmd.Transaction = context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
      cmd.CommandText = "SELECT " + fence.Hold("@p", "@e");
      cmd.Parameters.Add(PerspectiveWriteFence.PartitionParameter("p", tenure));
      cmd.Parameters.Add(PerspectiveWriteFence.EpochParameter("e", tenure));
      _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    } catch (PostgresException ex) when (PerspectiveWriteFence.IsFenced(ex)) {
      Report(context, 1);
      throw new PartitionOwnershipLostException(streamId);
    } finally {
      await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
  }

  /// <summary>Counts and logs <paramref name="count"/> fenced writes; nothing for none.</summary>
  internal static void Report(DbContext context, int count) {
    if (count == 0 || _host(context) is not { } host) {
      return;
    }
    host.GetService<WorkCoordinatorMetrics>()?.FencedWrites.Add(
      count, new KeyValuePair<string, object?>(WorkCoordinatorMetrics.KIND_TAG, KIND));
    if (host.GetService<ILoggerFactory>()?.CreateLogger(LOGGER_CATEGORY) is { } logger) {
      LogFenced(logger, count);
    }
  }

  private static IServiceProvider? _host(DbContext context) =>
    context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.ApplicationServiceProvider;

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
    Message = "Fenced {Count} perspective write(s): the stream's partition is no longer owned at the epoch its work was claimed under, so the current owner applies it")]
  private static partial void LogFenced(ILogger logger, int count);
}
