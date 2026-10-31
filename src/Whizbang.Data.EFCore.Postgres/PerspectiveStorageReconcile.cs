// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The EF Core driver's start-time storage-parameter reconcile (#1747): the generated schema extensions call it at every
/// start, the fast path included, after the schema pass and the managed-object reconcile.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/JsonbColumnStorageIntegrationTests.cs:AStart_ResetsAnUndeclaredSetting_AndLeavesTheRestAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/JsonbColumnStorageIntegrationTests.cs:AManuallyManagedTable_IsLeftAlone_UntilItIsTakenOffTheListAsync</tests>
public static partial class PerspectiveStorageReconcile {
  /// <summary>
  /// Runs the reconcile on a connection of its own when one can be opened, otherwise on the context's. Never throws but
  /// for cancellation: a connection that cannot be opened is logged as a warning, and the start goes on.
  /// </summary>
  /// <param name="dbContext">The context whose schema was initialized.</param>
  /// <param name="schema">The schema, bare.</param>
  /// <param name="perspectiveSql">Each perspective's generated schema SQL.</param>
  /// <param name="connectionFactory">Opens a connection of the reconcile's own, or null to borrow the context's.</param>
  /// <param name="logger">Where the reconcile reports.</param>
  /// <param name="cancellationToken">Cancels the reconcile.</param>
  /// <returns>What the reconcile found and did.</returns>
  public static async Task<PerspectiveStorageReport> RunAsync(
      DbContext dbContext, string schema, IEnumerable<string> perspectiveSql, Func<NpgsqlConnection>? connectionFactory,
      ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(dbContext);
    logger ??= Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    var owned = connectionFactory?.Invoke();
    try {
      NpgsqlConnection connection;
      if (owned is not null) {
        await owned.OpenAsync(cancellationToken).ConfigureAwait(false);
        connection = owned;
      } else {
        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
      }
      return await PerspectiveStorageParameters.ReconcileAsync(
        connection, schema, perspectiveSql, logger, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogConnectionFailed(logger, schema, ex.Message);
      return PerspectiveStorageReport.Empty;
    } finally {
      if (owned is not null) {
        await owned.DisposeAsync().ConfigureAwait(false);
      } else {
        await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
      }
    }
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Perspective storage ({Schema}): the storage settings were not reconciled; the next start tries again: {Error}")]
  private static partial void LogConnectionFailed(ILogger logger, string schema, string error);
}
