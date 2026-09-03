using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Versioning;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The Postgres <see cref="IStartupAssessor"/>: compares this binary's library version against
/// every version the migration ledger records, and produces the <c>Assess</c> verdict before
/// anything is changed. A read — no lock, no transaction, no DDL — cheap enough that every
/// instance doing it costs nothing.
/// </summary>
/// <remarks>
/// Ordering is SemVer precedence on <c>wh_schema_versions.library_version</c>: pre-release
/// precedence is the common path (everything before 1.0 ships with a pre-release label), numeric
/// identifiers compare numerically (<c>alpha.10</c> is newer than <c>alpha.2</c>), build metadata
/// is ignored. An unparseable own version is not guessed at — the verdict is
/// <see cref="StartupVerdict.StandDown"/>, because every wrong answer at this point is worse than
/// stopping. An unparseable recorded version cannot outrank anything (the same stance
/// <c>MigrationVersionGuard</c> takes: unreadable history does not block).
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
  public async Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) {
    IReadOnlyList<string> recorded;
    try {
      recorded = await _readRecordedVersionsAsync(cancellationToken).ConfigureAwait(false);
    } catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) {
      // No ledger at all — the fresh-database verdict, not an error.
      return new StartupAssessment(StartupVerdict.Migrate, "fresh database — no migration ledger yet");
    }
    return ComputeVerdict(_versionProvider?.LibraryVersion, recorded);
  }

  /// <summary>
  /// The verdict table as a pure function: this binary's version against every recorded one.
  /// Exposed for exhaustive unit coverage — the IO around it is a single SELECT.
  /// </summary>
  internal static StartupAssessment ComputeVerdict(string? mine, IReadOnlyList<string> recorded) {
    if (mine is null) {
      // Absent and unreadable are different facts (issue #619): nobody registered a provider, which
      // the Postgres driver now always does — a host that bypasses the driver must register
      // LibraryVersionProvider itself. Naming the registration is what makes the stand-down diagnosable.
      return new StartupAssessment(StartupVerdict.StandDown,
        "no ILibraryVersionProvider is registered, so this binary's own library version is unknown — "
        + "refusing to migrate; every wrong answer here is worse than stopping. The Postgres driver "
        + "registers one; a host that bypasses it must register LibraryVersionProvider itself");
    }
    if (!SemanticVersion.TryParse(mine, out var myVersion)) {
      return new StartupAssessment(StartupVerdict.StandDown,
        $"own library version '{mine}' is unreadable — refusing to migrate; every wrong answer here is worse than stopping");
    }

    if (recorded.Count == 0) {
      return new StartupAssessment(StartupVerdict.Migrate, "fresh database — nothing recorded in the ledger");
    }

    string? newest = null;
    var newestParsed = default(SemanticVersion);
    var hasNewest = false;
    foreach (var version in recorded) {
      if (!SemanticVersion.TryParse(version, out var parsed)) {
        continue;   // unreadable history cannot outrank anything — same stance as the ledger guard
      }
      if (!hasNewest || parsed.CompareTo(newestParsed) > 0) {
        newest = version;
        newestParsed = parsed;
        hasNewest = true;
      }
    }

    if (hasNewest && newestParsed.CompareTo(myVersion) > 0) {
      return new StartupAssessment(StartupVerdict.StandDown,
        $"the ledger records version {newest}, newer than this binary's {mine} — standing down: never apply anything, hold the data plane, report not-ready-while-alive");
    }

    return new StartupAssessment(StartupVerdict.Serve,
      hasNewest
        ? $"schema recorded at {newest}; this binary runs {mine} — nothing newer recorded"
        : $"only unreadable versions recorded; this binary runs {mine} — unreadable history does not block");
  }

  private async Task<IReadOnlyList<string>> _readRecordedVersionsAsync(CancellationToken cancellationToken) {
    using var scope = _scopeFactory.CreateScope();
    var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(_dbContextType);
    var schema = dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    var prefix = string.IsNullOrWhiteSpace(schema) || schema == "public" ? "" : $"\"{schema}\".";

    await using var connectionScope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
      (NpgsqlConnection)dbContext.Database.GetDbConnection(), cancellationToken).ConfigureAwait(false);
    await using var cmd = connectionScope.Connection.CreateCommand();
#pragma warning disable S2077 // schema comes from the EF model, not user input — same pattern as the coordinator
    cmd.CommandText = $@"
      SELECT DISTINCT v.library_version
      FROM {prefix}wh_schema_migrations m
      JOIN {prefix}wh_schema_versions v ON v.id = m.version_id
      WHERE m.owner = 'whizbang'";
#pragma warning restore S2077

    var versions = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      if (!reader.IsDBNull(0)) {
        versions.Add(reader.GetString(0));
      }
    }
    return versions;
  }
}
