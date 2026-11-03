// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The Postgres <see cref="IStartupAssessor"/>: compares this binary's library version against
/// every version the migration ledger records, and produces the <c>Assess</c> verdict before
/// anything is changed. A read — no lock, no transaction, no DDL — cheap enough that every
/// instance doing it costs nothing.
/// </summary>
/// <remarks>
/// Ordering is SemVer precedence on <c>wh_schema_versions.library_version</c>, shared with the Dapper driver
/// (<see cref="PostgresStartupAssessment"/>). The verdict is read once per host, through
/// <see cref="StartupAssessmentSource"/>, by every reader of it.
/// </remarks>
/// <docs>operations/startup/rolling-upgrades#assess</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupAssessorTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ColdBootJourneyE2ETests.cs</tests>
public sealed class EFCorePostgresStartupAssessor : IStartupAssessor {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly Type _dbContextType;
  private readonly ILibraryVersionProvider? _versionProvider;

  /// <summary>Creates the assessor over the consumer's DbContext type, resolved per call.</summary>
  public EFCorePostgresStartupAssessor(
      IServiceScopeFactory scopeFactory, Type dbContextType, ILibraryVersionProvider? versionProvider = null) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(dbContextType);
    _scopeFactory = scopeFactory;
    _dbContextType = dbContextType;
    _versionProvider = versionProvider;
  }

  /// <inheritdoc />
  /// <remarks>
  /// The version comparison and the ledger read are the Postgres drivers' shared <see cref="PostgresStartupAssessment"/>.
  /// When nothing newer is recorded, the schema is Current if every DbContext this host registers answers that it needs
  /// nothing applied and nothing converted (#1992), read without a lock.
  /// </remarks>
  public async Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) {
    using var scope = _scopeFactory.CreateScope();
    var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(_dbContextType);
    var schema = dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();

    await using var connectionScope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
      (NpgsqlConnection)dbContext.Database.GetDbConnection(), cancellationToken).ConfigureAwait(false);
    return await PostgresStartupAssessment.AssessAsync(
      connectionScope.Connection, schema, _versionProvider?.LibraryVersion,
      ct => DbContextInitializationRegistry.IsSchemaCurrentAsync(scope.ServiceProvider, ct),
      cancellationToken).ConfigureAwait(false);
  }
}
