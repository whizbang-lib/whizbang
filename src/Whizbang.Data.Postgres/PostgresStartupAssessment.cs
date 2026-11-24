// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Versioning;

namespace Whizbang.Data.Postgres;

/// <summary>What an instance brings to the rollback gate (#2022, #2122, #2553).</summary>
/// <param name="Own">This instance's versions and id; a null library version means no provider says.</param>
/// <param name="Defined">What this build defines (#2553), produced only when a change applied by a newer version has to be
/// judged by its content: each change its schema pass records, by the ledger's name, with
/// the content hash it records; a recorded change this build defines identically is current, whoever applied it.</param>
/// <param name="Overrides">The operator's overrides, for each side (#2022, #2122, #2553; <see cref="UpgradeOrder.ReadOverrides"/>).</param>
internal sealed record StartupGateInput(
  VersionSighting Own, Func<IReadOnlyDictionary<string, string>> Defined, SchemaGateOverrides Overrides);

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
/// <c>applied_version_id</c> (359), which a pass that only skips or re-stamps never moves, so an application rolled back
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
  /// <param name="gate">This instance's versions, what its build defines, and the operator's overrides.</param>
  /// <param name="isCurrent">Whether the schema needs nothing applied, asked only when nothing newer is recorded or
  /// online. A throw is read as "could not be shown current".</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The verdict.</returns>
  internal static async Task<StartupAssessment> AssessAsync(
      NpgsqlConnection connection, string? schema, StartupGateInput gate, Func<CancellationToken, Task<bool>> isCurrent,
      CancellationToken cancellationToken) {
    var prefix = PgIdentifier.QualifyPrefix(schema);
    var probe = await ProbeAsync(connection, prefix, cancellationToken).ConfigureAwait(false);
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
    var verdict = _computeVerdict(gate.Own, ledger, online, gate.Overrides, gate.Defined);
    if (verdict.Verdict != StartupVerdict.Serve) {
      return verdict;
    }

    bool current;
    try {
      current = await isCurrent(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Unanswerable is not current: the full path runs, as it always has, and finds out for itself.
      return verdict with { Reason = verdict.Reason + "; the schema could not be shown current (" + TypeNameFormatter.SimpleName(ex.GetType()) + ")" };
    }
    return current
      ? verdict with { Verdict = StartupVerdict.Current, Reason = verdict.Reason + "; the schema is current: nothing to apply or convert" }
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
    var probe = await ProbeAsync(connection, prefix, cancellationToken).ConfigureAwait(false);
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
  /// <param name="overrides">The operator's overrides, for each side (#2022, #2122, #2553); none when null. An
  /// incompatibility override serves its side as it is, with the reason it would have stood down in
  /// <see cref="StartupAssessment.OverriddenReason"/>. It never lifts the stand-down for a newer version online, nor the
  /// refusal of a build that cannot say what it is.</param>
  /// <param name="defined">What this build defines (#2553), by the ledger's name, with each content hash; null when
  /// nothing is known.</param>
  /// <returns>Migrate (with the rollback owed, when one is), Serve or StandDown; Current only when the override let an
  /// incompatible instance start.</returns>
  internal static StartupAssessment ComputeVerdict(
      VersionSighting own, IReadOnlyList<AppliedSchemaChange> ledger, IReadOnlyList<VersionSighting> online,
      SchemaGateOverrides? overrides = null, IReadOnlyDictionary<string, string>? defined = null) =>
    _computeVerdict(own, ledger, online, overrides, () => defined);

  /// <summary>
  /// The verdict, with what this build defines produced only when some change applied by a newer version has to be
  /// judged by its content (#2553): judging by content only ever clears a change, so a ledger nothing newer applied
  /// needs no definitions at all, and a driver whose definitions cannot be produced fails no start it would not fail.
  /// </summary>
  private static StartupAssessment _computeVerdict(
      VersionSighting own, IReadOnlyList<AppliedSchemaChange> ledger, IReadOnlyList<VersionSighting> online,
      SchemaGateOverrides? overrides, Func<IReadOnlyDictionary<string, string>?> defined) {
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

    // A newer version online is the cluster-wide stand-down, not an incompatibility: no override lifts it.
    if (UpgradeOrder.FindNewer(own, online) is { } newer) {
      return new StartupAssessment(StartupVerdict.StandDown, newer.Describe() + STANDING_DOWN);
    }

    // #2553, owner decision 2: the W! migrations and the application's schema are judged apart, each with its own verdict.
    var gate = overrides ?? SchemaGateOverrides.None;
    var definitions = new Lazy<IReadOnlyDictionary<string, string>?>(defined);
    var platform = JudgeSide(SchemaChangeKind.Platform, own, ledger, () => definitions.Value, gate);
    var application = JudgeSide(SchemaChangeKind.Application, own, ledger, () => definitions.Value, gate);
    if (platform.OverriddenReason is not null && application.Verdict == StartupVerdict.Migrate) {
      // The W! migrations are served as they are, so nothing is applied, and the application's rollback cannot run.
      application = new SchemaSideVerdict(SchemaChangeKind.Application, StartupVerdict.StandDown,
        $"{application.Reason}, which does not run while the W! migrations are served as they are; set "
        + $"{UpgradeOrder.APPLICATION_OVERRIDE_INCOMPATIBILITY_KEY} to true to serve the application schema as it is too");
    }
    return _combine(own, ledger, platform, application, gate);
  }

  private const string STANDING_DOWN = "; standing down: never apply anything, hold the data plane, report not-ready-while-alive";

  /// <summary>
  /// One side's verdict (#2553): the changes of that side applied since this version (judged by content, #2553 owner
  /// decision 1), rolled back when every one can be reversed, else served as it is when the side's own override is on,
  /// else a stand-down naming the side's changes.
  /// </summary>
  /// <param name="side">The side judged.</param>
  /// <param name="own">This instance's versions.</param>
  /// <param name="ledger">Every change the ledger records; only the side's own are judged.</param>
  /// <param name="defined">Produces what this build defines, asked only when a change of the side was applied by a newer
  /// version; null when nothing is known.</param>
  /// <param name="overrides">The operator's overrides.</param>
  /// <returns>Serve (nothing applied since), Migrate with the rollback owed, Current when overridden, or StandDown.</returns>
  internal static SchemaSideVerdict JudgeSide(
      SchemaChangeKind side, VersionSighting own, IReadOnlyList<AppliedSchemaChange> ledger,
      Func<IReadOnlyDictionary<string, string>?> defined, SchemaGateOverrides overrides) {
    // By who applied each change first: the content rule only clears changes from that set, so nothing in it means
    // nothing to ask the build about.
    var byVersion = UpgradeOrder.AppliedSince(own, ledger.Where(c => c.Kind == side), null);
    var since = byVersion.Count == 0 ? byVersion : UpgradeOrder.AppliedSince(own, byVersion, defined());
    if (since.Count == 0) {
      return new SchemaSideVerdict(side, StartupVerdict.Serve, "nothing applied since this version");
    }
    var allowLossy = overrides.AllowsLossy(side);
    if (UpgradeOrder.WhyNotReversible(own, since, allowLossy) is not { } refusal) {
      return new SchemaSideVerdict(side, StartupVerdict.Migrate, UpgradeOrder.DescribeRollback(own, since)) {
        Rollback = new RollbackPlan(since, allowLossy),
      };
    }
    return overrides.Overrides(side)
      ? new SchemaSideVerdict(side, StartupVerdict.Current,
          $"served as it is, with nothing applied to it, because {UpgradeOrder.OverrideKeyFor(side)} (or "
          + $"{UpgradeOrder.OVERRIDE_INCOMPATIBILITY_KEY} for both sides) is on: {refusal}") { OverriddenReason = refusal }
      : new SchemaSideVerdict(side, StartupVerdict.StandDown, refusal);
  }

  /// <summary>
  /// The instance's verdict from the two sides' (#2553): it serves only when both can. W! first: a W! stand-down (or W!
  /// served as it is) runs nothing; an application stand-down lets the W! migrations run, then stands the instance down.
  /// </summary>
  private static StartupAssessment _combine(
      VersionSighting own, IReadOnlyList<AppliedSchemaChange> ledger, SchemaSideVerdict platform, SchemaSideVerdict application,
      SchemaGateOverrides overrides) {
    SchemaSideVerdict[] sides = [platform, application];
    var rollback = _rollback(platform, application, overrides);
    if (sides.Any(s => s.Verdict == StartupVerdict.StandDown)) {
      return new StartupAssessment(StartupVerdict.StandDown, _reason(sides, s => s.Reason) + STANDING_DOWN) {
        Platform = platform,
        Application = application,
        Rollback = rollback,
      };
    }
    if (sides.Any(s => s.OverriddenReason is not null)) {
      // The same reason the stand-down would have given, recorded on the instance's row and logged at every start.
      var wouldStandDown = _reason(sides, s => s.OverriddenReason ?? s.Reason) + STANDING_DOWN;
      // W! served as it is applies nothing at all (Current); the application's schema served as it is leaves the W!
      // migrations to run, with the rollback they owe.
      if (platform.OverriddenReason is not null) {
        return new StartupAssessment(StartupVerdict.Current, _reason(sides, s => s.Reason)) {
          OverriddenReason = wouldStandDown,
          Platform = platform,
          Application = application,
        };
      }
      return new StartupAssessment(rollback is null ? StartupVerdict.Serve : StartupVerdict.Migrate, _reason(sides, s => s.Reason)) {
        OverriddenReason = wouldStandDown,
        Platform = platform,
        Application = application,
        Rollback = rollback,
      };
    }
    if (rollback is not null) {
      return new StartupAssessment(StartupVerdict.Migrate, _reason(sides, s => s.Reason)) {
        Platform = platform,
        Application = application,
        Rollback = rollback,
      };
    }
    var newest = UpgradeOrder.NewestLibrary(ledger.Select(v => v.LibraryVersion));
    return new StartupAssessment(StartupVerdict.Serve,
      newest is not null
        ? $"schema recorded at {newest}; this binary runs {own.LibraryVersion}; nothing applied since this version, and nothing newer online"
        : $"only unreadable versions recorded; this binary runs {own.LibraryVersion}; unreadable history does not block") {
      Platform = platform,
      Application = application,
    };
  }

  /// <summary>Each side with something to say, by name: <c>W! migrations: ...; application schema: ...</c>.</summary>
  private static string _reason(IEnumerable<SchemaSideVerdict> sides, Func<SchemaSideVerdict, string> text) =>
    string.Join("; ", sides.Where(s => s.Verdict != StartupVerdict.Serve).Select(s => $"{s.Name}: {text(s)}"));

  /// <summary>
  /// The rollback this start runs (#2122, #2553): the changes of each side that runs, each side with its own lossy
  /// setting. Nothing when the W! migrations do not run (they stood down, or are served as they are), and the
  /// application's changes only when its schema runs too.
  /// </summary>
  private static RollbackPlan? _rollback(SchemaSideVerdict platform, SchemaSideVerdict application, SchemaGateOverrides overrides) {
    if (platform.Verdict is not (StartupVerdict.Migrate or StartupVerdict.Serve)) {
      return null;
    }
    var sides = new Dictionary<SchemaChangeKind, bool> { [SchemaChangeKind.Platform] = overrides.AllowLossyPlatform };
    List<AppliedSchemaChange> changes = [.. platform.Rollback?.Changes ?? []];
    if (application.Verdict is StartupVerdict.Migrate or StartupVerdict.Serve) {
      sides[SchemaChangeKind.Application] = overrides.AllowLossyApplication;
      changes.AddRange(application.Rollback?.Changes ?? []);
    }
    return changes.Count == 0
      ? null
      : new RollbackPlan(changes, sides.Values.Any(lossy => lossy)) { Sides = sides };
  }

  /// <summary>What the database has: the ledger, its version table and owner column, and what reading who is online needs.</summary>
  internal readonly record struct Probe(
    bool Ledger, bool Versions, bool Owner, bool LedgerApplication, bool Online, bool AppliedBy, bool StoredForms, bool TemporalForms,
    bool Reversals, bool StoredFormHashes, bool TemporalFormHashes);

  internal static async Task<Probe> ProbeAsync(NpgsqlConnection connection, string prefix, CancellationToken cancellationToken) {
    await using var probe = connection.CreateCommand();
    probe.CommandText = """
      SELECT to_regclass($1) IS NOT NULL,
             to_regclass($2) IS NOT NULL,
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($1) AND attname = 'owner' AND NOT attisdropped),
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($2) AND attname = 'application_version' AND NOT attisdropped),
             to_regprocedure($3) IS NOT NULL
               AND EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($4) AND attname = 'application_version' AND NOT attisdropped),
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($1) AND attname = 'applied_version_id' AND NOT attisdropped),
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($5) AND attname = 'applied_library_version' AND NOT attisdropped),
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($6) AND attname = 'applied_library_version' AND NOT attisdropped),
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($1) AND attname = 'reversal' AND NOT attisdropped),
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($5) AND attname = 'content_hash' AND NOT attisdropped),
             EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($6) AND attname = 'content_hash' AND NOT attisdropped)
      """;
    probe.Parameters.AddWithValue(prefix + "wh_schema_migrations");
    probe.Parameters.AddWithValue(prefix + "wh_schema_versions");
    probe.Parameters.AddWithValue(prefix + "wh_live_instance_ids()");
    probe.Parameters.AddWithValue(prefix + "wh_service_instances");
    probe.Parameters.AddWithValue(prefix + StoredFormMigrationSql.JOURNAL);
    probe.Parameters.AddWithValue(prefix + "wh_perspective_forms");
    await using var reader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new Probe(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4),
      reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10));
  }

  /// <summary>One change the ledger records: the change, the version row that applied it, and its reverse script.</summary>
  internal sealed record LedgerChange(AppliedSchemaChange Change, int? AppliedVersionId, string? DownSql);

  private static async Task<IReadOnlyList<AppliedSchemaChange>> _readLedgerAsync(
      NpgsqlConnection connection, string prefix, Probe probe, CancellationToken cancellationToken) =>
    [.. (await ReadChangesAsync(connection, prefix, probe, withScripts: false, cancellationToken).ConfigureAwait(false)).Select(c => c.Change)];

  /// <summary>
  /// Every change the ledger records, with who applied it and how it is reversed (#2022, #2122); the reverse scripts
  /// too when <paramref name="withScripts"/>, which only the rollback reads.
  /// </summary>
  internal static async Task<IReadOnlyList<LedgerChange>> ReadChangesAsync(
      NpgsqlConnection connection, string prefix, Probe probe, bool withScripts, CancellationToken cancellationToken) {
    // #2022: who applied each change (359), or on an older ledger the version that last stamped it. Every row is read,
    // so a ledger whose rows were all re-stamped is still a ledger; a row whose applier is unknown carries no versions.
    // A row not written since 359 has no applied_version_id: one last applied (status 1, 2, 4) was applied by its
    // version_id, as every row of an older ledger is taken to be.
    var applier = probe.AppliedBy
      ? "COALESCE(m.applied_version_id, CASE WHEN m.status IN (1, 2, 4) THEN m.version_id END)"
      : "m.version_id";
    var application = probe.LedgerApplication ? "v.application_version" : "NULL::text";
    var (kind, notStep) = probe.Owner
      ? ("m.owner IN ('perspective', 'association')", "m.owner <> 'step'")
      : ("(m.file_name LIKE 'perspective:%' OR m.file_name LIKE 'app:%')", "m.file_name NOT LIKE 'step:%'");
    // #2122: how each change is reversed (386); nothing recorded is irreversible.
    var reversal = probe.Reversals ? "m.reversal" : "NULL::text";
    var script = probe.Reversals && withScripts ? "m.down_sql" : "NULL::text";
    await using var command = connection.CreateCommand();
#pragma warning disable S2077 // the prefix is quoted by PgIdentifier; the fragments are this method's own literals
    command.CommandText = $"""
      SELECT m.file_name AS name, {kind} AS application, v.library_version, {application} AS application_version,
             {reversal} AS reversal, v.id AS applied_version_id, {script} AS down_sql, m.content_hash, false AS by_name
      FROM {prefix}wh_schema_migrations m
      LEFT JOIN {prefix}wh_schema_versions v ON v.id = {applier}
      WHERE {notStep}
      {_conversions(prefix, probe)}
      ORDER BY name
      """;
#pragma warning restore S2077
    var changes = new List<LedgerChange>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      // The versions are NULL when nobody is known to have applied the row: DBNull reads as null.
      var change = new AppliedSchemaChange(reader.GetString(0),
        reader.GetBoolean(1) ? SchemaChangeKind.Application : SchemaChangeKind.Platform,
        reader.GetValue(2) as string, reader.GetValue(3) as string) {
        Reversal = MigrationReversal.FromLedgerText(reader.GetValue(4) as string),
        // #2553: what was applied, which the gate compares with what this build defines under the same name; a conversion
        // recorded before its journal held hashes is identified by its name.
        ContentHash = reader.GetValue(7) as string,
        NameIdentifiesContent = reader.GetBoolean(8),
      };
      changes.Add(new LedgerChange(change, reader.GetValue(5) as int?, reader.GetValue(6) as string));
    }
    return changes;
  }

  /// <summary>
  /// The stored-form conversions as changes (#2131): each conversion a known version applied, from the journal that records
  /// it (385). An application schema change, since the library converts what the application's model declares. Nothing
  /// on a journal older than the record, and a conversion nobody known applied is not read. Each carries the content hash
  /// its journal recorded (570), and is identified by its name when it has none (#2553).
  /// </summary>
  private static string _conversions(string prefix, Probe probe) {
    var union = new StringBuilder();
    if (probe.StoredForms) {
      union.Append(CultureInfo.InvariantCulture, $"""
        UNION ALL
        SELECT 'stored-form:' || j.name, true, j.applied_library_version, j.applied_application_version, NULL, NULL::int, NULL,
               {(probe.StoredFormHashes ? "j.content_hash" : "NULL")}, true
        FROM {prefix}{StoredFormMigrationSql.JOURNAL} j WHERE j.applied_library_version IS NOT NULL

        """);
    }
    if (probe.TemporalForms) {
      union.Append(CultureInfo.InvariantCulture, $"""
        UNION ALL
        SELECT 'temporal-form:' || f.table_name, true, f.applied_library_version, f.applied_application_version, NULL, NULL::int, NULL,
               {(probe.TemporalFormHashes ? "f.content_hash" : "NULL")}, true
        FROM {prefix}wh_perspective_forms f WHERE f.applied_library_version IS NOT NULL

        """);
    }
    return union.ToString();
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
