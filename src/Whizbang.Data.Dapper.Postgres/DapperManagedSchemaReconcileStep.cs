// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The Dapper driver's maintenance step that runs the managed-object reconcile between starts, the same re-run the
/// EF Core driver has: a drop a start held back (a running instance still declared the object) lands once that
/// instance is gone, without waiting for the next deploy.
/// </summary>
/// <remarks>
/// Once per fleet per <see cref="ManagedSchemaHostPass.ClaimWindow"/>: only the instance that takes the window's claim
/// (<see cref="FleetClaim"/>) reconciles, and the reconcile takes the schema lock with a try, so it never waits on a
/// start that is migrating. It runs with this instance's id, the configured settings and every registered
/// contributor, exactly as the start does.
/// </remarks>
/// <param name="connectionString">The database the perspectives are in.</param>
/// <param name="declare">What the perspectives declare; called for a fresh set each run.</param>
/// <param name="logger">Where the reconcile reports what it dropped and kept.</param>
/// <param name="timeProvider">The clock the claim window is read from.</param>
/// <docs>fundamentals/perspectives/managed-schema-objects#every-later-start</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperManagedObjectsStartupTests.cs</tests>
public sealed class DapperManagedSchemaReconcileStep(
    string connectionString,
    Func<ManagedSchemaObjectSet> declare,
    ILogger<DapperManagedSchemaReconcileStep> logger,
    TimeProvider timeProvider) : IMaintenanceStep {

  /// <inheritdoc />
  public string Name => "managed-schema-objects";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var settings = ManagedSchemaSettings.Read(services.GetService<IConfiguration>());
    if (settings.Mode == ReconcileMode.Off) {
      return;
    }

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    string schema;
    await using (var current = new NpgsqlCommand("SELECT current_schema()", connection)) {
      schema = (string)(await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    var key = ManagedSchemaHostPass.ClaimKey(schema, timeProvider.GetUtcNow());
    if (!await FleetClaim.TryClaimAsync(
        services.GetService<IClaimedEmissionStore>(), connection, PgIdentifier.Quote(schema), key, cancellationToken)
        .ConfigureAwait(false)) {
      return;
    }

    await ManagedSchemaReconciler.RunAsync(
      connection, schema, ManagedSchemaHostPass.Declared(declare(), services), settings,
      ManagedSchemaHostPass.InstanceId(services), logger, cancellationToken).ConfigureAwait(false);
  }
}
