// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The one way a context's managed-object reconcile is run, by the schema initializer at every start and by the
/// maintenance step between starts: what the context declares, plus every registered contributor, reconciled
/// under the schema lock with the configured settings.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcileStepTests.cs</tests>
public static class ManagedSchemaReconcile {
  /// <summary>
  /// The settings for <paramref name="services"/>' configuration. Read before anything else, and never inside the
  /// reconcile's own failure handling, so a setting that is not one fails the caller.
  /// </summary>
  public static ManagedSchemaSettings Settings(IServiceProvider? services) =>
    ManagedSchemaSettings.Read(services?.GetService<IConfiguration>());

  /// <summary>Reconciles <paramref name="dbContext"/>'s schema, on a connection of its own when one can be opened.</summary>
  /// <param name="dbContext">The context whose perspective tables are reconciled.</param>
  /// <param name="manifest">The context's schema and what its generated schema declares.</param>
  /// <param name="settings">The settings, from <see cref="Settings"/>.</param>
  /// <param name="connectionFactory">Opens a connection outside the context's; null to borrow the context's.</param>
  /// <param name="services">Where contributors and this instance's id are resolved from; may be null.</param>
  /// <param name="logger">Where the report goes.</param>
  /// <param name="cancellationToken">Cancels the reconcile.</param>
  public static async Task<ManagedSchemaReport> RunAsync(
      DbContext dbContext, ManagedSchemaManifest manifest, ManagedSchemaSettings settings,
      Func<NpgsqlConnection>? connectionFactory, IServiceProvider? services, ILogger? logger,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(manifest);
    var declared = ManagedSchemaHostPass.Declared(manifest.Declare(), services);
    var instanceId = ManagedSchemaHostPass.InstanceId(services);

    // A connection of its own when one can be opened; otherwise the context's, borrowed and closed again.
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
      return await ManagedSchemaReconciler.RunAsync(
        connection, manifest.Schema, declared, settings, instanceId, logger, cancellationToken).ConfigureAwait(false);
    } finally {
      if (owned is not null) {
        await owned.DisposeAsync().ConfigureAwait(false);
      } else {
        await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
      }
    }
  }
}
