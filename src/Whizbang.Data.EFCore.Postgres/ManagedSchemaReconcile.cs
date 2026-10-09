// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;
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
  /// <param name="schema">The context's schema, bare (e.g. <c>public</c>).</param>
  /// <param name="declared">What the context's generated schema declares.</param>
  /// <param name="settings">The settings, from <see cref="Settings"/>.</param>
  /// <param name="connectionFactory">Opens a connection outside the context's; null to borrow the context's.</param>
  /// <param name="services">Where contributors and this instance's id are resolved from; may be null.</param>
  /// <param name="logger">Where the report goes.</param>
  /// <param name="cancellationToken">Cancels the reconcile.</param>
  public static async Task<ManagedSchemaReport> RunAsync(
      DbContext dbContext, string schema, ManagedSchemaObjectSet declared, ManagedSchemaSettings settings,
      Func<NpgsqlConnection>? connectionFactory, IServiceProvider? services, ILogger? logger,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(declared);
    foreach (var contributor in services?.GetServices<IManagedSchemaObjectContributor>() ?? []) {
      contributor.Contribute(declared);
    }
    var instanceId = services?.GetService<IServiceInstanceProvider>()?.InstanceId;
    var lockId = SchemaInitializationLockKey.Compute(schema);

    if (connectionFactory is not null) {
      await using var connection = connectionFactory();
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
      return await ManagedSchemaReconciler.RunAsync(
        connection, schema, declared, settings, lockId, instanceId, logger, cancellationToken).ConfigureAwait(false);
    }

    await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    try {
      return await ManagedSchemaReconciler.RunAsync(
        (NpgsqlConnection)dbContext.Database.GetDbConnection(), schema, declared, settings, lockId, instanceId, logger,
        cancellationToken).ConfigureAwait(false);
    } finally {
      await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
  }
}
