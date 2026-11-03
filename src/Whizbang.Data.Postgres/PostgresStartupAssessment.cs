// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Startup;
using Whizbang.Core.Versioning;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The <c>Assess</c> verdict both Postgres drivers produce (#1992): this binary's library version against every version
/// the migration ledger records, and, when nothing newer is recorded, whether the schema is already current. One read,
/// no lock, no transaction, no DDL.
/// </summary>
/// <remarks>
/// <para>
/// The ledger's existence is read first, from the catalog. No migration ledger at all is a brand-new database: there is
/// no version record and nothing newer can exist, so the verdict is Migrate and the bootstrap that follows (which
/// creates the ledger) is safe. A ledger with no version table records no version either, and migrates. A ledger that
/// exists and cannot be read is not mistaken for either: the read throws, and the caller decides whether that is
/// temporary (retried within Preflight) or fails startup.
/// </para>
/// <para>
/// The EF Core driver's ledger names each row's owner; the Dapper driver's has no owner column and every row in it is
/// the framework's, so it is read whole.
/// </para>
/// <para>
/// Ordering is SemVer precedence: pre-release precedence is the common path, numeric identifiers compare numerically
/// (<c>alpha.10</c> is newer than <c>alpha.2</c>), build metadata is ignored. An unparseable own version stands down,
/// because every wrong answer at that point is worse than stopping. An unparseable recorded version cannot outrank
/// anything.
/// </para>
/// </remarks>
/// <docs>operations/startup/rolling-upgrades#assess</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupAssessorTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStartupAssessorTests.cs</tests>
internal static class PostgresStartupAssessment {
  /// <summary>Reads the ledger and produces the verdict.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema the ledger lives in; null or empty for the connection's own.</param>
  /// <param name="mine">This binary's library version; null when no provider says.</param>
  /// <param name="isCurrent">Whether the schema needs nothing applied, asked only when nothing newer is recorded. A
  /// throw is read as "could not be shown current".</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The verdict.</returns>
  internal static async Task<StartupAssessment> AssessAsync(
      NpgsqlConnection connection, string? schema, string? mine, Func<CancellationToken, Task<bool>> isCurrent,
      CancellationToken cancellationToken) {
    var prefix = PgIdentifier.QualifyPrefix(schema);
    var (hasLedger, hasVersions, hasOwner) = await _probeAsync(connection, prefix, cancellationToken).ConfigureAwait(false);
    if (!hasLedger) {
      return new StartupAssessment(StartupVerdict.Migrate,
        "brand-new database: no version record, so nothing newer than this binary can exist");
    }
    if (!hasVersions) {
      return new StartupAssessment(StartupVerdict.Migrate,
        "the migration ledger records no versions, so nothing newer than this binary is recorded");
    }

    var verdict = ComputeVerdict(mine, await _readVersionsAsync(connection, prefix, hasOwner, cancellationToken).ConfigureAwait(false));
    if (verdict.Verdict != StartupVerdict.Serve) {
      return verdict;
    }

    bool current;
    try {
      current = await isCurrent(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Unanswerable is not current: the full path runs, as it always has, and finds out for itself.
      return verdict with { Reason = verdict.Reason + "; the schema could not be shown current (" + ex.GetType().Name + ")" };
    }
    return current
      ? new StartupAssessment(StartupVerdict.Current, verdict.Reason + "; the schema is current: nothing to apply or convert")
      : verdict;
  }

  /// <summary>
  /// The verdict table as a pure function: this binary's version against every recorded one. Exposed for exhaustive unit
  /// coverage; the IO around it is a single SELECT.
  /// </summary>
  /// <param name="mine">This binary's library version; null when no provider says.</param>
  /// <param name="recorded">Every library version the ledger records.</param>
  /// <returns>Migrate, Serve or StandDown; never Current, which needs the schema read.</returns>
  internal static StartupAssessment ComputeVerdict(string? mine, IReadOnlyList<string> recorded) {
    if (mine is null) {
      // Absent and unreadable are different facts (issue #619): nobody registered a provider, which the Postgres
      // drivers always do; a host that bypasses them must register LibraryVersionProvider itself.
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

  private static async Task<(bool Ledger, bool Versions, bool Owner)> _probeAsync(
      NpgsqlConnection connection, string prefix, CancellationToken cancellationToken) {
    await using var probe = connection.CreateCommand();
    probe.CommandText = """
      SELECT to_regclass($1) IS NOT NULL,
             to_regclass($2) IS NOT NULL,
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($1) AND attname = 'owner' AND NOT attisdropped)
      """;
    probe.Parameters.AddWithValue(prefix + "wh_schema_migrations");
    probe.Parameters.AddWithValue(prefix + "wh_schema_versions");
    await using var reader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
  }

  private static async Task<IReadOnlyList<string>> _readVersionsAsync(
      NpgsqlConnection connection, string prefix, bool hasOwner, CancellationToken cancellationToken) {
    await using var command = connection.CreateCommand();
#pragma warning disable S2077 // the prefix is quoted by PgIdentifier; nothing user-supplied is spliced
    command.CommandText = $"""
      SELECT DISTINCT v.library_version
      FROM {prefix}wh_schema_migrations m
      JOIN {prefix}wh_schema_versions v ON v.id = m.version_id
      {(hasOwner ? "WHERE m.owner = 'whizbang'" : string.Empty)}
      """;
#pragma warning restore S2077
    var versions = new List<string>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      versions.Add(reader.GetString(0));   // library_version is NOT NULL
    }
    return versions;
  }
}
