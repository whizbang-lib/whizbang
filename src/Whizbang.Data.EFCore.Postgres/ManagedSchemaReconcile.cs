// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The one way a context's managed-object reconcile is run, by the schema initializer once per schema per build and by
/// the maintenance step between starts, and the one way every start reports what it declares (#2608): what the context
/// declares, plus every registered contributor, reconciled under the schema lock with the configured settings.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcileStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/ManagedObjectReportParityTests.cs</tests>
public static class ManagedSchemaReconcile {
  /// <summary>
  /// The settings for <paramref name="services"/>' configuration. Read before anything else, and never inside the
  /// reconcile's own failure handling, so a setting that is not one fails the caller.
  /// </summary>
  public static ManagedSchemaSettings Settings(IServiceProvider? services) =>
    ManagedSchemaSettings.Read(services?.GetService<IConfiguration>());

  /// <summary>
  /// What this build gives the context's reconcile, as the text its once-per-build fingerprint is taken over (#2608):
  /// what the context and every registered contributor declare, and the settings.
  /// </summary>
  /// <param name="manifest">The context's schema and what its generated schema declares.</param>
  /// <param name="settings">The settings, from <see cref="Settings"/>.</param>
  /// <param name="services">Where contributors are resolved from; may be null.</param>
  /// <returns>The inputs, the same as the Dapper driver gives for the same declarations and settings.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkSessionsTests.cs</tests>
  public static string Inputs(ManagedSchemaManifest manifest, ManagedSchemaSettings settings, IServiceProvider? services) {
    ArgumentNullException.ThrowIfNull(manifest);
    return StartupWorkInputs.Of(ManagedSchemaHostPass.Declared(manifest.Declare(), services), settings);
  }

  /// <summary>
  /// Records what this instance declares for the fleet gate, and changes nothing else (#2608): what every start does,
  /// whether or not it is the start of its build that reconciles. See <see cref="ManagedSchemaReconciler.ReportAsync"/>.
  /// </summary>
  /// <param name="dbContext">The context whose declarations are recorded.</param>
  /// <param name="manifest">The context's schema and what its generated schema declares.</param>
  /// <param name="settings">The settings, from <see cref="Settings"/>.</param>
  /// <param name="connectionFactory">Opens a connection outside the context's; null to borrow the context's.</param>
  /// <param name="services">Where contributors and this instance's id are resolved from; may be null.</param>
  /// <param name="logger">Where a failed report is said.</param>
  /// <param name="cancellationToken">Cancels the report.</param>
  /// <returns>True when the record was written.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/ManagedObjectReportParityTests.cs</tests>
  public static Task<bool> ReportAsync(
      DbContext dbContext, ManagedSchemaManifest manifest, ManagedSchemaSettings settings,
      Func<NpgsqlConnection>? connectionFactory, IServiceProvider? services, ILogger? logger,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(manifest);
    var declared = ManagedSchemaHostPass.Declared(manifest.Declare(), services);
    var instanceId = ManagedSchemaHostPass.InstanceId(services);
    return _onConnectionAsync(dbContext, connectionFactory, connection => ManagedSchemaReconciler.ReportAsync(
      connection, manifest.Schema, declared, settings, instanceId, logger, cancellationToken), cancellationToken);
  }

  /// <summary>Reconciles <paramref name="dbContext"/>'s schema, on a connection of its own when one can be opened.</summary>
  /// <param name="dbContext">The context whose perspective tables are reconciled.</param>
  /// <param name="manifest">The context's schema and what its generated schema declares.</param>
  /// <param name="settings">The settings, from <see cref="Settings"/>.</param>
  /// <param name="connectionFactory">Opens a connection outside the context's; null to borrow the context's.</param>
  /// <param name="services">Where contributors and this instance's id are resolved from; may be null.</param>
  /// <param name="logger">Where the report goes.</param>
  /// <param name="cancellationToken">Cancels the reconcile.</param>
  public static Task<ManagedSchemaReport> RunAsync(
      DbContext dbContext, ManagedSchemaManifest manifest, ManagedSchemaSettings settings,
      Func<NpgsqlConnection>? connectionFactory, IServiceProvider? services, ILogger? logger,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(manifest);
    var declared = ManagedSchemaHostPass.Declared(manifest.Declare(), services);
    var instanceId = ManagedSchemaHostPass.InstanceId(services);
    return _onConnectionAsync(dbContext, connectionFactory, connection => ManagedSchemaReconciler.RunAsync(
      connection, manifest.Schema, declared, settings, instanceId, logger, cancellationToken), cancellationToken);
  }

  /// <summary>Runs <paramref name="work"/> on a connection of its own when one can be opened; otherwise on the context's, borrowed and closed again.</summary>
  private static async Task<T> _onConnectionAsync<T>(
      DbContext dbContext, Func<NpgsqlConnection>? connectionFactory, Func<NpgsqlConnection, Task<T>> work,
      CancellationToken cancellationToken) {
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
      return await work(connection).ConfigureAwait(false);
    } finally {
      if (owned is not null) {
        await owned.DisposeAsync().ConfigureAwait(false);
      } else {
        await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
      }
    }
  }
}
