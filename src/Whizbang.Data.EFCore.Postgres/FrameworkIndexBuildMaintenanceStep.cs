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
/// The maintenance step that builds the framework's concurrent indexes (<see cref="ConcurrentIndexBuild"/>, #2092):
/// after the migrations, outside any transaction, on the maintainer's turn, so writes to the indexed table go on while
/// it builds.
/// </summary>
/// <remarks>
/// Idle once every index is valid: one catalog read per index per cycle. A build waits for every older transaction and
/// reads the table twice, so the first cycle after an upgrade on a large store can take minutes; it has no timeout of its
/// own, and a build that is interrupted is dropped and started again on a later cycle.
/// </remarks>
/// <param name="dbContextType">The DbContext whose schema the indexes live in.</param>
/// <param name="logger">Optional logger.</param>
/// <docs>operations/infrastructure/migrations#indexes-built-after-the-migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ConcurrentIndexBuildTests.cs</tests>
public sealed class FrameworkIndexBuildMaintenanceStep(
    Type dbContextType,
    ILogger<FrameworkIndexBuildMaintenanceStep>? logger = null) : IMaintenanceStep {
  /// <summary>No timeout: a concurrent build blocks no write, and one that stops short starts over.</summary>
  private const int BUILD_TIMEOUT_SECONDS = 0;

  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));

  /// <inheritdoc />
  public string Name => "framework-index-build";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var context = (DbContext)services.GetRequiredService(_dbContextType);
    // The schema the work coordinator resolves for the same context, with its fallback for a model mapped without one.
    var schema = EFCoreWorkCoordinator<DbContext>.ResolveSchema(context.Model, typeof(OutboxRecord), null);
    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    try {
      var connection = (NpgsqlConnection)context.Database.GetDbConnection();
      foreach (var index in ConcurrentIndexBuild.FrameworkIndexes) {
        await ConcurrentIndexBuild.EnsureAsync(connection, schema, index, BUILD_TIMEOUT_SECONDS, logger, cancellationToken)
          .ConfigureAwait(false);
      }
    } finally {
      await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
  }
}
