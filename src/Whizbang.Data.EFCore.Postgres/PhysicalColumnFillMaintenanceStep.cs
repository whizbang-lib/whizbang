// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The maintenance step that completes perspective field moves: it fills promoted columns for rows written
/// with the value only in the document, settles demotions, and reports columns only a rebuild can restore.
/// </summary>
/// <remarks>
/// <para>
/// A field promoted to a physical column is filled from the document by the start that adds the column.
/// An instance still on the previous release, running beside it during a rolling deploy, does not know
/// the column and keeps writing rows whose column is null. Queries on the field read the column, so those
/// rows are missed by a filter and misplaced by a sort until their next event (issue #1009). This step
/// fills them; see <see cref="PhysicalColumnFill"/>.
/// </para>
/// <para>
/// Bounded, idempotent and done once per fleet. It does nothing unless the schema pass armed a column, so
/// a service that promotes nothing never claims. Otherwise only the instance that wins the window's claim
/// in <c>wh_unique_emission_claims</c> (the claim <c>PublishOnceAsync</c> uses) runs, and each run fills at
/// most a bounded number of batches. The claim goes through <see cref="FleetClaim"/>: the claim store when one is
/// registered, the claim table directly otherwise, as on the Dapper driver.
/// </para>
/// </remarks>
/// <param name="dbContextType">The DbContext whose schema this step looks in.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="timeProvider">The clock the claim window is read from.</param>
/// <param name="batchSize">The most rows one batch fills in one column.</param>
/// <param name="maxRoundsPerRun">The most batches one run fills per column.</param>
/// <param name="settle">How long a column stays watched after the start that added it.</param>
/// <docs>fundamentals/perspectives/physical-fields#rows-written-during-a-rolling-deploy</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFillMaintenanceStepTests.cs</tests>
public sealed partial class PhysicalColumnFillMaintenanceStep(
    Type dbContextType,
    ILogger<PhysicalColumnFillMaintenanceStep>? logger = null,
    TimeProvider? timeProvider = null,
    int batchSize = PhysicalColumnFill.DEFAULT_BATCH_SIZE,
    int maxRoundsPerRun = PhysicalColumnFill.DEFAULT_MAX_ROUNDS,
    TimeSpan? settle = null) : IMaintenanceStep {

  /// <summary>How long one instance's claim to run the step lasts: the fleet runs it once per window.</summary>
  public static readonly TimeSpan ClaimWindow = PhysicalColumnFill.ClaimWindow;

  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));
  private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
  private readonly TimeSpan _settle = settle ?? PhysicalColumnFill.DefaultSettle;

  /// <inheritdoc />
  public string Name => "physical-column-fill";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var context = (DbContext)services.GetRequiredService(_dbContextType);
    var schema = context.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    schema = string.IsNullOrWhiteSpace(schema) ? "public" : schema;
    var quotedSchema = PgIdentifier.Quote(schema);

    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    try {
      var connection = (NpgsqlConnection)context.Database.GetDbConnection();
      if (!await PhysicalColumnFill.AnyArmedAsync(connection, quotedSchema, cancellationToken).ConfigureAwait(false)) {
        return;
      }

      var key = PhysicalColumnFill.ClaimKey(schema, _timeProvider.GetUtcNow());
      if (!await FleetClaim.TryClaimAsync(
          services.GetService<IClaimedEmissionStore>(), connection, quotedSchema, key, cancellationToken)
          .ConfigureAwait(false)) {
        return;
      }

      await PhysicalColumnFill.RunAsync(
        connection, quotedSchema, batchSize, maxRoundsPerRun, _settle, _logger, cancellationToken).ConfigureAwait(false);
    } finally {
      await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
  }
}
