// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>What <see cref="EFCoreSchemaTemplate.EnsureTemplateAsync"/> found under the template's name.</summary>
internal enum TemplateOutcome {
  /// <summary>Nothing was there; the template was built.</summary>
  Built,

  /// <summary>A current template was there and is used as it is.</summary>
  Reused,

  /// <summary>Something stale was there (another schema, another marker, or not a finished template); it was replaced.</summary>
  Rebuilt,
}

/// <summary>
/// The migrated framework schema every <see cref="EFCoreTestBase"/> test starts from, built once as a PostgreSQL
/// template database and copied per test with <c>CREATE DATABASE ... TEMPLATE</c> (#2167).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a template.</b> Every base-class test used to create an empty database and run the whole schema pass on it:
/// the core tables, every migration, every perspective table, the constraints, the associations and the reconcile.
/// That pass was most of a shard's wall time. A copy of an already migrated database is a file-level clone (the
/// default <c>WAL_LOG</c> strategy, no checkpoint) with the template's indexes, rows and planner statistics, so each
/// test still owns a database it may change, without repeating the work.
/// </para>
/// <para>
/// <b>When it is rebuilt.</b> The name carries a hash, <see cref="Fingerprint"/>, of the schema texts the generated
/// code exposes (the migrations as the ledger hashes them, the bootstrap scripts with the core tables, and the
/// physical-column rewrites), so two worktrees with different migrations sharing one container each keep their own
/// template. The hash does not see everything the pass builds (perspective DDL and associations are private to the
/// generated class), so it is not trusted alone: once per process the template is copied to a probe and the schema
/// pass is run on the probe. The pass records a hash per migration, per perspective table and for the associations in
/// its ledger and rewrites a row whenever what the code would build differs, so a ledger the pass leaves unchanged is
/// a template that is the schema this build migrates. A template whose probe's ledger changes, or that carries another
/// fingerprint's marker, is dropped and built again.
/// </para>
/// <para>
/// Not <c>IsWhizbangSchemaCurrentAsync</c>: on a database one pass has just built it answers false, because the
/// stored-form rewrite runs before the pass creates the perspective tables and so records nothing as settled until a
/// second start. Every template would read as stale.
/// </para>
/// <para>
/// <b>Concurrency.</b> Test classes in one process share one build through <see cref="TemplateGate"/>. Processes
/// sharing the container (two build-server slots) serialize on a session advisory lock keyed by the template's name,
/// taken on a connection without pooling, so closing it releases the lock whatever happened. The schema is migrated
/// in a scratch database and renamed to the template's name only when complete (create, then rename), so a template
/// under that name is never half built. <c>CREATE DATABASE ... TEMPLATE</c> fails with 55006 while anything is
/// connected to the source, so the build connects without pooling, ends any backend left before the rename, and the
/// finished template refuses connections (<c>ALLOW_CONNECTIONS false</c>): nothing can open one by accident later.
/// </para>
/// <para>
/// <b>Leftovers.</b> A build that fails drops its scratch database; one that dies leaves it to the factory's hourly
/// sweep, since it is named like any scratch database. Templates live on in a developer's long-lived container, so
/// each use stamps the template's comment, and <see cref="SweepStaleTemplatesAsync"/> drops the run prefix's other
/// templates once unused for a day.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Containers/EFCoreSchemaTemplateTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TestIsolation/SchemaTemplateGuardTests.cs</tests>
internal static partial class EFCoreSchemaTemplate {
  /// <summary>The run's templates are named under this prefix.</summary>
  internal const string TEMPLATE_PREFIX = "eftmpl";

  /// <summary>How much of the fingerprint the name carries: 128 bits, inside the 63-byte identifier limit.</summary>
  internal const int NAME_HASH_LENGTH = 32;

  /// <summary>Bumped when how a template is built changes in a way its inputs do not show.</summary>
  private const string FORMAT_VERSION = "1";

  /// <summary>The scratch database a template is migrated in before it takes its name.</summary>
  private const string BUILD_PREFIX = "eftmplbuild";

  /// <summary>The copy a reused template is checked through, so nothing ever connects to the template itself.</summary>
  private const string PROBE_PREFIX = "eftmplprobe";

  /// <summary>Precedes the fingerprint in the comment on a template's <c>public</c> schema, which every copy carries.</summary>
  private const string MARKER_PREFIX = "whizbang-ef-template:";

  /// <summary>How the last use is written into the template's database comment.</summary>
  private const string STAMP_FORMAT = "yyyyMMddHHmmss";

  /// <summary>How long another template of the run prefix may go unused before the sweep drops it.</summary>
  internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

  /// <summary>
  /// The columns a migration seeds with a value of the database's own that leaves the database, renewed in every
  /// copy by <see cref="ReidentifyStatements"/>.
  /// </summary>
  /// <remarks>
  /// <c>wh_service_config.service_id</c> is the service's identity: it rides on every outgoing envelope as the source
  /// service, so two test databases sharing it (an origin and a consumer in one test) would read each other's events
  /// as their own. Found by machine, not by reading the migrations: two databases migrated from scratch are compared
  /// column by column, date and time columns left out, and what differs has to be exactly this list and
  /// <see cref="LocalRowKeys"/>.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Containers/EFCoreSchemaTemplateTests.cs:TwoMigrationsFromScratch_DifferOnlyInWhatACopyRenewsAsync</tests>
  internal static readonly (string Table, string Column)[] RenewedPerCopy = [("wh_service_config", "service_id")];

  /// <summary>
  /// Random primary keys a migration gives its seed rows, which mean nothing outside the database and so are left as
  /// the template has them.
  /// </summary>
  /// <remarks>
  /// Rows are found by their natural keys (message type and target, CLR type and table); the generated id is only
  /// the row's own handle within one database, and two test databases never exchange it.
  /// </remarks>
  internal static readonly (string Table, string Column)[] LocalRowKeys = [
    ("wh_message_associations", "id"),
    ("wh_perspective_registry", "id"),
  ];

  /// <summary>What a copy runs to give the columns in <see cref="RenewedPerCopy"/> values of its own.</summary>
  internal static readonly string[] ReidentifyStatements = [
    "UPDATE public.wh_service_config SET service_id = gen_random_uuid(), created_at = now()",
  ];

  private static readonly Lazy<string> _fingerprint = new(ComputeFingerprint);

  private static readonly TemplateGate _gate = new(_ensureRunTemplateAsync);

  [GeneratedRegex("^[a-z0-9_]{1,63}$")]
  private static partial Regex _plainIdentifier();

  /// <summary>The hash of the schema texts this build migrates; see the remarks for what it covers.</summary>
  internal static string Fingerprint => _fingerprint.Value;

  /// <summary>The name of the template this run's tests copy.</summary>
  internal static string TemplateName => $"{TEMPLATE_PREFIX}_{Fingerprint[..NAME_HASH_LENGTH]}";

  /// <summary>Hashes the schema texts the generated initializer exposes.</summary>
  /// <returns>A lower-case hex SHA-256.</returns>
  internal static string ComputeFingerprint() {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    void add(string part) {
      hash.AppendData(Encoding.UTF8.GetBytes(part));
      hash.AppendData([0]);
    }

    add(FORMAT_VERSION);
    foreach (var (name, text) in WorkCoordinationDbContextSchemaExtensions.GetHashedMigrationTexts()) {
      add(name);
      add(text);
    }
    foreach (var (name, sql) in WorkCoordinationDbContextSchemaExtensions.GetBootstrapScripts()) {
      add(name);
      add(sql);
    }
    foreach (var (name, sql) in WorkCoordinationDbContextSchemaExtensions.GetPhysicalColumnRewrites()) {
      add(name);
      add(sql);
    }
    return Convert.ToHexStringLower(hash.GetHashAndReset());
  }

  /// <summary>The comment a template built for <paramref name="fingerprint"/> leaves on its <c>public</c> schema.</summary>
  /// <param name="fingerprint">The fingerprint the template was built for.</param>
  /// <returns>The marker text.</returns>
  internal static string MarkerFor(string fingerprint) => MARKER_PREFIX + fingerprint;

  /// <summary>Writes a moment the way a template's last use is stamped.</summary>
  /// <param name="when">The moment.</param>
  /// <returns>The stamp.</returns>
  internal static string FormatStamp(DateTimeOffset when) =>
    when.UtcDateTime.ToString(STAMP_FORMAT, CultureInfo.InvariantCulture);

  /// <summary>Reads a stamp <see cref="FormatStamp"/> wrote.</summary>
  /// <param name="comment">A template's database comment.</param>
  /// <param name="stamp">The moment, when the comment is a stamp.</param>
  /// <returns><see langword="true"/> when the comment is a stamp.</returns>
  internal static bool TryReadStamp(string? comment, out DateTimeOffset stamp) {
    var read = DateTime.TryParseExact(comment, STAMP_FORMAT, CultureInfo.InvariantCulture,
      DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when);
    stamp = read ? new DateTimeOffset(when, TimeSpan.Zero) : default;
    return read;
  }

  /// <summary>Ensures this run's template exists and is current, once per process.</summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The template's name.</returns>
  internal static Task<string> EnsureAsync(CancellationToken cancellationToken) => _gate.GetAsync(cancellationToken);

  /// <summary>Creates a database for one test as a copy of this run's template.</summary>
  /// <param name="namePrefix">A short prefix naming the fixture.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The database and its connection string.</returns>
  internal static async Task<PerTestDatabase> CreateDatabaseAsync(string namePrefix, CancellationToken cancellationToken = default) {
    var template = await EnsureAsync(cancellationToken);
    var database = await PerTestDatabaseFactory.CreateFromTemplateAsync(namePrefix, template, cancellationToken);
    // The copy is a database of its own, so it gets its own values where a migration seeded one per database.
    var connectionString = new NpgsqlConnectionStringBuilder(PerTestDatabaseFactory.ForSetup(database.ConnectionString)) {
      Pooling = false,
    }.ConnectionString;
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    foreach (var statement in ReidentifyStatements) {
      await _executeAsync(connection, statement, cancellationToken);
    }
    return database;
  }

  private static async Task<string> _ensureRunTemplateAsync(CancellationToken cancellationToken) {
    var name = TemplateName;
    await EnsureTemplateAsync(name, Fingerprint,
      (context, ct) => context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: ct), cancellationToken);
    await SweepStaleTemplatesAsync(TEMPLATE_PREFIX, name, StaleAfter, DateTimeOffset.UtcNow, cancellationToken);
    return name;
  }

  /// <summary>
  /// Ensures a current template named <paramref name="templateName"/> exists, building or rebuilding it under the
  /// cross-process lock.
  /// </summary>
  /// <param name="templateName">The template's name: a plain lower-case identifier.</param>
  /// <param name="fingerprint">What the template is built for, left on it as its marker.</param>
  /// <param name="migrate">Migrates the scratch database the template is built in.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>What was found under the name.</returns>
  internal static async Task<TemplateOutcome> EnsureTemplateAsync(
      string templateName,
      string fingerprint,
      Func<WorkCoordinationDbContext, CancellationToken, Task> migrate,
      CancellationToken cancellationToken) {
    if (!_plainIdentifier().IsMatch(templateName)) {
      // The name goes into DDL, which takes no parameters.
      throw new ArgumentException($"'{templateName}' is not a plain lower-case identifier.", nameof(templateName));
    }

    // No pooling: the session advisory lock goes with the connection when it closes, however this method ends.
    await using var admin = new NpgsqlConnection(_adminConnectionString());
    await admin.OpenAsync(cancellationToken);
    await _executeAsync(admin, "SELECT pg_advisory_lock(@key)", cancellationToken, ("key", _lockKey(templateName)));

    var isTemplate = await _isTemplateAsync(admin, templateName, cancellationToken);
    if (isTemplate == true && await _copyIsCurrentAsync(templateName, fingerprint, migrate, cancellationToken)) {
      await _stampAsync(admin, templateName, cancellationToken);
      return TemplateOutcome.Reused;
    }

    var outcome = TemplateOutcome.Built;
    if (isTemplate is not null) {
      await _dropAsync(admin, templateName, cancellationToken);
      outcome = TemplateOutcome.Rebuilt;
    }
    await _buildAsync(admin, templateName, fingerprint, migrate, cancellationToken);
    return outcome;
  }

  /// <summary>
  /// Drops the templates under <paramref name="prefix"/>, other than <paramref name="keep"/>, last used longer ago
  /// than <paramref name="staleAfter"/>.
  /// </summary>
  /// <param name="prefix">The prefix the templates are named under.</param>
  /// <param name="keep">The caller's own template, never dropped.</param>
  /// <param name="staleAfter">How long unused before a template is dropped.</param>
  /// <param name="now">The moment ages are measured from.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many were dropped.</returns>
  /// <remarks>
  /// A template whose comment is not a stamp was not stamped by this code and is left alone, as the scratch sweep
  /// leaves a name it does not recognize. Every user stamps its template before using it, so a template another
  /// process or worktree is using is never a day old.
  /// </remarks>
  internal static async Task<int> SweepStaleTemplatesAsync(
      string prefix, string keep, TimeSpan staleAfter, DateTimeOffset now, CancellationToken cancellationToken) {
    await using var admin = new NpgsqlConnection(_adminConnectionString());
    await admin.OpenAsync(cancellationToken);

    var stale = new List<string>();
    await using (var query = new NpgsqlCommand("""
        SELECT datname, shobj_description(oid, 'pg_database')
        FROM pg_database
        WHERE datistemplate AND starts_with(datname, @templatePrefix) AND datname <> @kept
        """, admin)) {
      _ = query.Parameters.AddWithValue("templatePrefix", prefix + "_");
      _ = query.Parameters.AddWithValue("kept", keep);
      await using var reader = await query.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) {
        var comment = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (TryReadStamp(comment, out var lastUsed) && lastUsed < now - staleAfter) {
          stale.Add(reader.GetString(0));
        }
      }
    }

    foreach (var name in stale) {
      await _dropAsync(admin, name, cancellationToken);
    }
    return stale.Count;
  }

  private static string _adminConnectionString() =>
    new NpgsqlConnectionStringBuilder(PerTestDatabaseFactory.ForSetup(SharedPostgresContainer.ConnectionString)) {
      Pooling = false,
    }.ConnectionString;

  /// <summary>A process-stable key per template name, so a build blocks only the callers of the same template.</summary>
  private static long _lockKey(string templateName) =>
    BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("whizbang-ef-template/" + templateName)));

  private static async Task _executeAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken,
      params (string Name, object Value)[] parameters) {
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var (name, value) in parameters) {
      _ = command.Parameters.AddWithValue(name, value);
    }
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private static async Task<bool?> _isTemplateAsync(NpgsqlConnection admin, string databaseName, CancellationToken cancellationToken) {
    await using var query = new NpgsqlCommand("SELECT datistemplate FROM pg_database WHERE datname = @name", admin);
    _ = query.Parameters.AddWithValue("name", databaseName);
    return await query.ExecuteScalarAsync(cancellationToken) as bool?;
  }

  /// <summary>
  /// Whether a copy of the template carries this fingerprint's marker and is left unchanged by the schema pass; the
  /// copy is dropped either way.
  /// </summary>
  private static async Task<bool> _copyIsCurrentAsync(
      string name,
      string fingerprint,
      Func<WorkCoordinationDbContext, CancellationToken, Task> migrate,
      CancellationToken cancellationToken) {
    var probe = await PerTestDatabaseFactory.CreateFromTemplateAsync(PROBE_PREFIX, name, cancellationToken);
    try {
      var connectionString = new NpgsqlConnectionStringBuilder(PerTestDatabaseFactory.ForSetup(probe.ConnectionString)) {
        Pooling = false,
      }.ConnectionString;
      if (await _scalarTextAsync(connectionString, "SELECT obj_description('public'::regnamespace, 'pg_namespace')",
          cancellationToken) != MarkerFor(fingerprint)) {
        return false;
      }
      var before = await _scalarTextAsync(connectionString, LEDGER, cancellationToken);
      var (dataSource, options) = EFCoreTestBase.CreateContextOptions(connectionString);
      await using (dataSource) {
        await using var context = new WorkCoordinationDbContext(options);
        await migrate(context, cancellationToken);
      }
      return before == await _scalarTextAsync(connectionString, LEDGER, cancellationToken);
    } finally {
      await PerTestDatabaseFactory.DropAsync(probe.Name, cancellationToken: CancellationToken.None);
    }
  }

  /// <summary>
  /// The schema ledger's content hashes, the pass's own step rows left out: a row per migration, per perspective
  /// table and for the associations, each rewritten when what the code would build differs.
  /// </summary>
  internal const string LEDGER = """
    SELECT coalesce(string_agg(coalesce(owner, '') || '|' || file_name || '|' || content_hash, E'\n' ORDER BY owner, file_name), '')
    FROM public.wh_schema_migrations
    WHERE owner IS DISTINCT FROM 'step'
    """;

  private static async Task<string?> _scalarTextAsync(string connectionString, string sql, CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(sql, connection);
    return await command.ExecuteScalarAsync(cancellationToken) as string;
  }

  private static Task _stampAsync(NpgsqlConnection admin, string name, CancellationToken cancellationToken) =>
    _executeAsync(admin, $"COMMENT ON DATABASE {name} IS '{FormatStamp(DateTimeOffset.UtcNow)}'", cancellationToken);

  /// <summary>Drops a template, or whatever holds its name, retrying the contention a copy in progress causes.</summary>
  private static Task _dropAsync(NpgsqlConnection admin, string name, CancellationToken cancellationToken) =>
    PerTestDatabaseFactory.RunWithRetryAsync(
      async () => {
        // A template cannot be dropped; IF EXISTS on both, since another process's sweep may get there first.
        await _executeAsync(admin,
          $"DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_database WHERE datname = '{name}') THEN "
          + $"ALTER DATABASE {name} WITH IS_TEMPLATE false; END IF; END $$", cancellationToken);
        await _executeAsync(admin, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)", cancellationToken);
      },
      static _ => false,
      TimeProvider.System,
      cancellationToken);

  private static async Task _buildAsync(
      NpgsqlConnection admin,
      string name,
      string fingerprint,
      Func<WorkCoordinationDbContext, CancellationToken, Task> migrate,
      CancellationToken cancellationToken) {
    var build = await PerTestDatabaseFactory.CreateAsync(BUILD_PREFIX, cancellationToken: cancellationToken);
    var holder = build.Name;
    try {
      // No pooling: a pooled session left in the build would block the rename and every copy (55006).
      var connectionString = new NpgsqlConnectionStringBuilder(PerTestDatabaseFactory.ForSetup(build.ConnectionString)) {
        Pooling = false,
        IncludeErrorDetail = true,
      }.ConnectionString;
      var (dataSource, options) = EFCoreTestBase.CreateContextOptions(connectionString);
      await using (dataSource) {
        await using var context = new WorkCoordinationDbContext(options);
        await migrate(context, cancellationToken);
      }
      await using (var connection = new NpgsqlConnection(connectionString)) {
        await connection.OpenAsync(cancellationToken);
        await _executeAsync(connection,
          $"COMMENT ON SCHEMA public IS '{MarkerFor(fingerprint).Replace("'", "''", StringComparison.Ordinal)}'",
          cancellationToken);
      }

      // A backend ends a moment after its client closes, and a rename refuses a database anyone is in (55006).
      await PerTestDatabaseFactory.RunWithRetryAsync(
        async () => {
          await _executeAsync(admin,
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @build AND pid <> pg_backend_pid()",
            cancellationToken, ("build", build.Name));
          await _executeAsync(admin, $"ALTER DATABASE {build.Name} RENAME TO {name}", cancellationToken);
        },
        static _ => false,
        TimeProvider.System,
        cancellationToken);
      holder = name;

      // Stamped before it is marked a template, so the sweep never sees a template without a stamp.
      await _stampAsync(admin, name, cancellationToken);
      await _executeAsync(admin, $"ALTER DATABASE {name} WITH IS_TEMPLATE true ALLOW_CONNECTIONS false", cancellationToken);
    } catch {
      // Whatever holds the work drops with the failure, so the next caller builds afresh; the failure is rethrown.
      await PerTestDatabaseFactory.DropAsync(holder, cancellationToken: CancellationToken.None);
      throw;
    }
  }
}

/// <summary>
/// One template build per process, shared by every test that arrives while it runs; a failure is not remembered.
/// </summary>
/// <param name="ensure">Builds or finds the template and returns its name.</param>
/// <remarks>
/// A gate that latched a failed attempt would hand every later test the same exception, or a name for a template
/// that was never built; so only a success is kept, and the next caller after a failure tries again.
/// </remarks>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Containers/EFCoreSchemaTemplateTests.cs</tests>
internal sealed class TemplateGate(Func<CancellationToken, Task<string>> ensure) {
  private readonly Lock _gate = new();
  private Task<string>? _attempt;

  /// <summary>The template's name, built on the first call that succeeds.</summary>
  /// <param name="cancellationToken">Bounds this caller's wait, and the attempt when this caller starts it.</param>
  /// <returns>The template's name.</returns>
  /// <remarks>
  /// Callers that arrive while an attempt runs share it. A finished attempt is kept only when it succeeded: one that
  /// failed or was canceled is replaced by the next caller's.
  /// </remarks>
  public Task<string> GetAsync(CancellationToken cancellationToken) {
    Task<string> attempt;
    lock (_gate) {
      if (_attempt is not { IsFaulted: false, IsCanceled: false }) {
        _attempt = ensure(cancellationToken);
      }
      attempt = _attempt;
    }
    return attempt.WaitAsync(cancellationToken);
  }
}
