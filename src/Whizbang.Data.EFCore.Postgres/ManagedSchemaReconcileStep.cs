// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The maintenance step that runs the managed-object reconcile between starts, so a drop held back at a start
/// (the previous release was still running and declared the object) happens once that release is gone, without
/// waiting for the next deploy.
/// </summary>
/// <remarks>
/// <para>
/// Run once per fleet per window: only the instance that wins the window's claim in
/// <c>wh_unique_emission_claims</c> reconciles (<see cref="FleetClaim"/>: through the claim store when one is
/// registered, directly in the table otherwise), and the reconcile itself takes the schema lock with a try, so it
/// never waits on a start that is migrating.
/// </para>
/// </remarks>
/// <param name="dbContextType">The DbContext whose manifest this step reconciles.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="timeProvider">The clock the claim window is read from.</param>
/// <docs>fundamentals/perspectives/managed-schema-objects#every-later-start</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcileStepTests.cs</tests>
public sealed partial class ManagedSchemaReconcileStep(
    Type dbContextType,
    ILogger<ManagedSchemaReconcileStep>? logger = null,
    TimeProvider? timeProvider = null) : IMaintenanceStep {

  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));
  private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  public string Name => "managed-schema-objects";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    if (services.GetKeyedService<ManagedSchemaManifest>(_dbContextType) is not { } manifest) {
      LogNoManifest(_logger, _dbContextType.Name);
      return;
    }
    var settings = ManagedSchemaReconcile.Settings(services);
    if (settings.Mode == ReconcileMode.Off) {
      return;
    }
    var context = (DbContext)services.GetRequiredService(_dbContextType);
    var key = ManagedSchemaHostPass.ClaimKey(manifest.Schema, _timeProvider.GetUtcNow());
    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    bool claimed;
    try {
      claimed = await FleetClaim.TryClaimAsync(
        services.GetService<IClaimedEmissionStore>(), (NpgsqlConnection)context.Database.GetDbConnection(),
        PgIdentifier.Quote(manifest.Schema), key, cancellationToken).ConfigureAwait(false);
    } finally {
      await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
    if (!claimed) {
      return;
    }

    await ManagedSchemaReconcile.RunAsync(
      context, manifest, settings,
      SchemaBoundaryConnections.Resolve(context, initConnectionString: null, services), services, _logger,
      cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Managed-object step skipped: {DbContext} registered no managed-object manifest")]
  private static partial void LogNoManifest(ILogger logger, string dbContext);
}
