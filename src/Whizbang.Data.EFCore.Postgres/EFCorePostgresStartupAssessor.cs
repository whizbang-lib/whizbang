// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Versioning;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The Postgres <see cref="IStartupAssessor"/>: compares this instance's versions against every version the migration
/// ledger records and every instance online (#2022), and produces the <c>Assess</c> verdict before
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
public sealed class EFCorePostgresStartupAssessor : IStartupAssessor, IOnlineVersionReader {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly Type _dbContextType;
  private readonly ILibraryVersionProvider? _versionProvider;
  private readonly IApplicationVersionProvider? _applicationVersion;
  private readonly IServiceInstanceProvider? _instance;
  private readonly bool _overrideIncompatibility;
  private readonly bool _allowLossyRollback;

  /// <summary>Creates the assessor over the consumer's DbContext type, resolved per call.</summary>
  /// <param name="scopeFactory">Scopes the DbContext each read is made through.</param>
  /// <param name="dbContextType">The consumer's DbContext type.</param>
  /// <param name="versionProvider">This binary's library version; none means it is unknown, which stands down.</param>
  /// <param name="applicationVersion">This instance's application version (#2022); none means it is unknown.</param>
  /// <param name="instance">This instance's identity (#2022): its own row is never a newer instance.</param>
  /// <param name="overrideIncompatibility">Whether the incompatibility override is on (#2022): read by the driver's
  /// registration from <see cref="UpgradeOrder.OVERRIDE_INCOMPATIBILITY_KEY"/>.</param>
  /// <param name="allowLossyRollback">Whether a rollback may discard data a newer version wrote (#2122): read by the
  /// driver's registration from <see cref="UpgradeOrder.ALLOW_LOSSY_ROLLBACK_KEY"/>.</param>
  public EFCorePostgresStartupAssessor(
      IServiceScopeFactory scopeFactory, Type dbContextType, ILibraryVersionProvider? versionProvider = null,
      IApplicationVersionProvider? applicationVersion = null, IServiceInstanceProvider? instance = null,
      bool overrideIncompatibility = false, bool allowLossyRollback = false) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(dbContextType);
    _scopeFactory = scopeFactory;
    _dbContextType = dbContextType;
    _versionProvider = versionProvider;
    _applicationVersion = applicationVersion;
    _instance = instance;
    _overrideIncompatibility = overrideIncompatibility;
    _allowLossyRollback = allowLossyRollback;
  }

  /// <inheritdoc />
  /// <remarks>
  /// The version comparison and the reads are the Postgres drivers' shared <see cref="PostgresStartupAssessment"/>.
  /// When nothing newer is recorded or online, the schema is Current if every DbContext this host registers answers
  /// that it needs nothing applied and nothing converted (#1992), read without a lock.
  /// </remarks>
  public Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) =>
    _readAsync((connection, schema, services) => PostgresStartupAssessment.AssessAsync(
      connection, schema,
      new VersionSighting(_versionProvider?.LibraryVersion, _applicationVersion?.ApplicationVersion.Recorded, _instance?.InstanceId),
      _overrideIncompatibility,
      _allowLossyRollback,
      ct => DbContextInitializationRegistry.IsSchemaCurrentAsync(services, ct),
      cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<VersionSighting>> ReadOnlineAsync(CancellationToken cancellationToken) =>
    _readAsync((connection, schema, _) => PostgresStartupAssessment.ReadOnlineAsync(connection, schema, cancellationToken),
      cancellationToken);

  private async Task<T> _readAsync<T>(
      Func<NpgsqlConnection, string?, IServiceProvider, Task<T>> read, CancellationToken cancellationToken) {
    using var scope = _scopeFactory.CreateScope();
    var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(_dbContextType);
    var schema = dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();

    await using var connectionScope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
      (NpgsqlConnection)dbContext.Database.GetDbConnection(), cancellationToken).ConfigureAwait(false);
    return await read(connectionScope.Connection, schema, scope.ServiceProvider).ConfigureAwait(false);
  }
}
