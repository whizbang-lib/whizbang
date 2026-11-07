// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Startup;
using Whizbang.Core.Versioning;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The <c>Assess</c> verdict both Postgres drivers produce (#1992): this instance's versions against every version the
/// migration ledger records and every instance online (#2022), and, when nothing newer is recorded or online, whether the
/// schema is already current. Reads only: no lock, no transaction, no DDL.
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
/// Ordering is the one version rule (<see cref="UpgradeOrder"/>, #2022): both the library and the application version,
/// the application first. An unparseable own library version stands down, because every wrong answer at that point is
/// worse than stopping. An unparseable recorded version cannot outrank anything.
/// </para>
/// <para>
/// The ledger is read through the rollback gate (#2022, owner decision 3): an instance may serve only when nothing was
/// applied after its own version, since a migration cannot be reversed yet (#2122). Who applied each change is
/// <c>applied_version_id</c> (351), which a pass that only skips or re-stamps never moves, so an application rolled back
/// with nothing migrated since serves normally. A ledger older than that record has only <c>version_id</c>, the version
/// that last stamped each row, and it is taken as the applier: the conservative reading, and the one #1992 made. Step
/// rows (a pass's progress) are not changes. Each row is a W! migration or an application schema change by its owner
/// (EF Core: <c>perspective</c> and <c>association</c> are the application's) or, in a ledger with no owner column, by
/// its name (<c>perspective:</c> and <c>app:</c>).
/// </para>
/// <para>
/// Online (#2022) is live by the one liveness rule (<c>wh_live_instance_ids</c>), not evicted, and serving: the row
/// records AcceptingCommands or Running. A newer instance that is still starting does not stand the fleet down before it
/// can take the work over, and one that died, stopped or stood down no longer does. This one verdict is what startup
/// reads and what a running instance asks again (the standby watcher), so an instance that starts while a newer one is
/// online learns from the durable record, and one that is running learns the same answer when it is told to ask. On a
/// schema older than the liveness rule (316), nothing is online that records an application version, and the ledger
/// alone decides.
/// </para>
/// </remarks>
/// <docs>operations/startup/rolling-upgrades#one-verdict</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupAssessorTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStartupAssessorTests.cs</tests>
internal static class PostgresStartupAssessment {
  /// <summary>The recorded phases of an instance that serves: what makes a live instance online.</summary>
  private static readonly string[] _serving = [nameof(Whizbang.Core.RunControl.LifecyclePhase.AcceptingCommands), nameof(Whizbang.Core.RunControl.LifecyclePhase.Running)];

  /// <summary>Reads the ledger and the instances online, and produces the verdict.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema the ledger lives in; null or empty for the connection's own.</param>
  /// <param name="own">This instance's versions and id; a null library version means no provider says.</param>
  /// <param name="isCurrent">Whether the schema needs nothing applied, asked only when nothing newer is recorded or
  /// online. A throw is read as "could not be shown current".</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The verdict.</returns>
  internal static async Task<StartupAssessment> AssessAsync(
      NpgsqlConnection connection, string? schema, VersionSighting own, Func<CancellationToken, Task<bool>> isCurrent,
      CancellationToken cancellationToken) {
    var prefix = PgIdentifier.QualifyPrefix(schema);
    var probe = await _probeAsync(connection, prefix, cancellationToken).ConfigureAwait(false);
    if (!probe.Ledger) {
      return new StartupAssessment(StartupVerdict.Migrate,
        "brand-new database: no version record, so nothing newer than this binary can exist");
    }
    if (!probe.Versions) {
      return new StartupAssessment(StartupVerdict.Migrate,
        "the migration ledger records no versions, so nothing newer than this binary is recorded");
    }

    var ledger = await _readLedgerAsync(connection, prefix, probe, cancellationToken).ConfigureAwait(false);
    var online = probe.Online
      ? await _readOnlineAsync(connection, prefix, cancellationToken).ConfigureAwait(false)
      : [];
    var verdict = ComputeVerdict(own, ledger, online);
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
  /// Reads every instance online (#2022), for the newer instance's announcement: the same read <see cref="AssessAsync"/>
  /// makes. Empty on a schema older than the one liveness rule.
  /// </summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema the instance table lives in; null or empty for the connection's own.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The online instances.</returns>
  internal static async Task<IReadOnlyList<VersionSighting>> ReadOnlineAsync(
      NpgsqlConnection connection, string? schema, CancellationToken cancellationToken) {
    var prefix = PgIdentifier.QualifyPrefix(schema);
    var probe = await _probeAsync(connection, prefix, cancellationToken).ConfigureAwait(false);
    return probe.Online ? await _readOnlineAsync(connection, prefix, cancellationToken).ConfigureAwait(false) : [];
  }

  /// <summary>
  /// The verdict table over library versions alone, each a W! migration recorded at that version; for the table's own
  /// tests.
  /// </summary>
  /// <param name="mine">This binary's library version; null when no provider says.</param>
  /// <param name="recorded">Every library version the ledger records as having applied a migration.</param>
  /// <returns>Migrate, Serve or StandDown.</returns>
  internal static StartupAssessment ComputeVerdict(string? mine, IReadOnlyList<string> recorded) =>
    ComputeVerdict(new VersionSighting(mine, null),
      [.. recorded.Select((v, i) => new AppliedSchemaChange($"migration {i + 1} recorded at {v}", SchemaChangeKind.Platform, v, null))], []);

  /// <summary>
  /// The verdict table as a pure function: the rollback gate over the ledger's changes, then this instance's versions
  /// against the online instances', through the one version rule. Exposed for exhaustive unit coverage; the IO around
  /// it is two SELECTs.
  /// </summary>
  /// <param name="own">This instance's versions and id.</param>
  /// <param name="ledger">Every change the ledger records, with who applied it (null versions when unknown).</param>
  /// <param name="online">Every instance online.</param>
  /// <returns>Migrate, Serve or StandDown; never Current, which needs the schema read.</returns>
  internal static StartupAssessment ComputeVerdict(
      VersionSighting own, IReadOnlyList<AppliedSchemaChange> ledger, IReadOnlyList<VersionSighting> online) {
    var mine = own.LibraryVersion;
    if (mine is null) {
      // Absent and unreadable are different facts (issue #619): nobody registered a provider, which the Postgres
      // drivers always do; a host that bypasses them must register LibraryVersionProvider itself.
      return new StartupAssessment(StartupVerdict.StandDown,
        "no ILibraryVersionProvider is registered, so this binary's own library version is unknown; "
        + "refusing to migrate; every wrong answer here is worse than stopping. The Postgres driver "
        + "registers one; a host that bypasses it must register LibraryVersionProvider itself");
    }
    if (!UpgradeOrder.IsReadable(mine)) {
      return new StartupAssessment(StartupVerdict.StandDown,
        $"own library version '{mine}' is unreadable, so this instance refuses to migrate; every wrong answer here is worse than stopping");
    }

    if (ledger.Count == 0) {
      return new StartupAssessment(StartupVerdict.Migrate, "fresh database: nothing recorded in the ledger");
    }

    const string STANDING_DOWN = "; standing down: never apply anything, hold the data plane, report not-ready-while-alive";
    if (UpgradeOrder.AppliedSince(own, ledger) is { Count: > 0 } since) {
      return new StartupAssessment(StartupVerdict.StandDown, UpgradeOrder.DescribeAppliedSince(own, since) + STANDING_DOWN);
    }
    if (UpgradeOrder.FindNewer(own, online) is { } newer) {
      return new StartupAssessment(StartupVerdict.StandDown, newer.Describe() + STANDING_DOWN);
    }

    var newest = UpgradeOrder.NewestLibrary(ledger.Select(v => v.LibraryVersion));
    return new StartupAssessment(StartupVerdict.Serve,
      newest is not null
        ? $"schema recorded at {newest}; this binary runs {mine}; nothing applied since this version, and nothing newer online"
        : $"only unreadable versions recorded; this binary runs {mine}; unreadable history does not block");
  }

  /// <summary>What the database has: the ledger, its version table and owner column, and what reading who is online needs.</summary>
  private readonly record struct Probe(bool Ledger, bool Versions, bool Owner, bool LedgerApplication, bool Online, bool AppliedBy);

  private static async Task<Probe> _probeAsync(NpgsqlConnection connection, string prefix, CancellationToken cancellationToken) {
    await using var probe = connection.CreateCommand();
    probe.CommandText = """
      SELECT to_regclass($1) IS NOT NULL,
             to_regclass($2) IS NOT NULL,
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($1) AND attname = 'owner' AND NOT attisdropped),
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($2) AND attname = 'application_version' AND NOT attisdropped),
             to_regprocedure($3) IS NOT NULL
               AND EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($4) AND attname = 'application_version' AND NOT attisdropped),
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($1) AND attname = 'applied_version_id' AND NOT attisdropped)
      """;
    probe.Parameters.AddWithValue(prefix + "wh_schema_migrations");
    probe.Parameters.AddWithValue(prefix + "wh_schema_versions");
    probe.Parameters.AddWithValue(prefix + "wh_live_instance_ids()");
    probe.Parameters.AddWithValue(prefix + "wh_service_instances");
    await using var reader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new Probe(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4),
      reader.GetBoolean(5));
  }

  private static async Task<IReadOnlyList<AppliedSchemaChange>> _readLedgerAsync(
      NpgsqlConnection connection, string prefix, Probe probe, CancellationToken cancellationToken) {
    // #2022: who applied each change (351), or on an older ledger the version that last stamped it. Every row is read,
    // so a ledger whose rows were all re-stamped is still a ledger; a row whose applier is unknown carries no versions.
    var applier = probe.AppliedBy ? "m.applied_version_id" : "m.version_id";
    var application = probe.LedgerApplication ? "v.application_version" : "NULL::text";
    var (kind, notStep) = probe.Owner
      ? ("m.owner IN ('perspective', 'association')", "m.owner <> 'step'")
      : ("(m.file_name LIKE 'perspective:%' OR m.file_name LIKE 'app:%')", "m.file_name NOT LIKE 'step:%'");
    await using var command = connection.CreateCommand();
#pragma warning disable S2077 // the prefix is quoted by PgIdentifier; the fragments are this method's own literals
    command.CommandText = $"""
      SELECT m.file_name, {kind}, v.library_version, {application}
      FROM {prefix}wh_schema_migrations m
      LEFT JOIN {prefix}wh_schema_versions v ON v.id = {applier}
      WHERE {notStep}
      ORDER BY m.file_name
      """;
#pragma warning restore S2077
    var changes = new List<AppliedSchemaChange>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      // The versions are NULL when nobody is known to have applied the row: DBNull reads as null.
      changes.Add(new AppliedSchemaChange(reader.GetString(0),
        reader.GetBoolean(1) ? SchemaChangeKind.Application : SchemaChangeKind.Platform,
        reader.GetValue(2) as string, reader.GetValue(3) as string));
    }
    return changes;
  }

  private static async Task<IReadOnlyList<VersionSighting>> _readOnlineAsync(
      NpgsqlConnection connection, string prefix, CancellationToken cancellationToken) {
    await using var command = connection.CreateCommand();
#pragma warning disable S2077 // the prefix is quoted by PgIdentifier; nothing user-supplied is spliced
    // #2022: online is live by the one liveness rule, not evicted, and serving.
    command.CommandText = $"""
      SELECT i.instance_id, i.library_version, i.application_version
      FROM {prefix}wh_service_instances i
      WHERE i.instance_id IN (SELECT l.instance_id FROM {prefix}wh_live_instance_ids() l)
        AND i.lifecycle_phase = ANY($1)
        AND NOT EXISTS (SELECT 1 FROM {prefix}wh_instance_evictions e WHERE e.instance_id = i.instance_id)
      ORDER BY i.instance_id
      """;
#pragma warning restore S2077
    command.Parameters.AddWithValue(_serving);
    var online = new List<VersionSighting>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      // Either version may be NULL (an older release recorded no application version): DBNull reads as null.
      online.Add(new VersionSighting(reader.GetValue(1) as string, reader.GetValue(2) as string, reader.GetGuid(0)));
    }
    return online;
  }
}
