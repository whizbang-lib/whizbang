using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// That the bootstrap really does leave a database able to elect a migrator, starting from nothing.
/// </summary>
/// <remarks>
/// <para>
/// Schema initialization has a cycle in it: choosing which instance migrates is a duty election,
/// the elector records its win through <c>record_capability</c>, and a migration creates that
/// function. A marked subset of the migrations is applied first to break the cycle, and the only
/// honest test of whether that subset is complete is to run it against an empty schema and then try
/// to elect.
/// </para>
/// <para>
/// So these run against a database of their own, created and dropped per test. Running them
/// against the shared, already-migrated database would prove nothing at all: every object would
/// already be there no matter what the bootstrap did or failed to do, and the test would stay green
/// through a closure that was missing half of itself.
/// </para>
/// <para>
/// The closure is also not obvious from reading the migrations, which is the other reason this
/// exists. Three of the four headers state dependencies that are wrong or incomplete: the
/// registration migration says it needs files that do not create what it uses, the eviction
/// migration points forward at files that come after it, and the table every one of them depends on
/// is not created by a migration at all.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaBootstrapPhase.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class SchemaBootstrapPhaseTests {
  private const long LOCK_ID = 573948201;
  private const int TIMEOUT_SECONDS = 60;
  private const string SCHEMA = "public";

  private string _databaseName = null!;
  private string _connectionString = null!;

  /// <summary>An instance identity, as a starting pod would present one.</summary>
  private sealed class _Instance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "bootstrap-svc";
    public string HostName => "bootstrap-host";
    public int ProcessId => 4242;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  /// <summary>A logger that keeps what it was told.</summary>
  private sealed class _RecordingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new _Scope();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
    private sealed class _Scope : IDisposable { public void Dispose() { } }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"bootstrap_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }

    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is null) {
      return;
    }

    try {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await using var drop = new NpgsqlCommand(
        $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
      await drop.ExecuteNonQueryAsync();
    } catch (NpgsqlException) {
      // The container goes with the run; a database left behind costs nothing.
    }
  }

  private NpgsqlConnection _connect() => new(_connectionString);

  private async Task<T?> _scalarAsync<T>(string sql) {
    await using var db = _connect();
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    var result = await command.ExecuteScalarAsync();
    return result is null or DBNull ? default : (T)result;
  }

  /// <summary>
  /// The bootstrap scripts a real service applies, taken from the generated initializer itself.
  /// </summary>
  /// <remarks>
  /// Not a copy. Assembling the list here would prove this file's idea of the closure complete and
  /// say nothing about the one that ships, which is the only thing worth knowing: the marker
  /// positions in the SQL, the migration order, and the schema transform all reach production
  /// through this method and nowhere else.
  /// </remarks>
  private static List<(string Name, string Sql)> _bootstrapScripts() =>
    WorkCoordinationDbContextSchemaExtensions.GetBootstrapScripts();

  /// <summary>
  /// An empty database can elect a migrator once the bootstrap has run.
  /// </summary>
  /// <remarks>
  /// The headline, and the only test that can actually catch an incomplete closure. Both halves are
  /// asserted end to end rather than by looking for objects: the instance registers, and
  /// <c>record_capability</c> — the function the elector calls, and the one that used to make this
  /// impossible — answers TRUE.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task AnEmptyDatabaseCanElectAfterTheBootstrapAsync(CancellationToken cancellationToken) {
    var ready = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    await Assert.That(ready).IsTrue()
      .Because("the bootstrap's whole purpose is that an election becomes possible after it");

    var instance = new _Instance();
    await SchemaBootstrapPhase.RegisterInstanceAsync(
      _connect, SCHEMA, instance, cancellationToken: cancellationToken);

    var recorded = await _scalarAsync<bool>(
      $"SELECT record_capability('{instance.InstanceId}'::uuid, 'migrator')");
    await Assert.That(recorded).IsTrue()
      .Because("this is the exact call the elector makes, and the one that could not be made "
        + "before the schema was migrated");
  }

  /// <summary>
  /// Without the bootstrap, that same election is impossible.
  /// </summary>
  /// <remarks>
  /// The characterization, and the reason the bootstrap exists. On an untouched database the
  /// capability function is simply absent, so an elector asking for a duty gets an error rather
  /// than a refusal. A test that only proved the bootstrap works would not show that anything
  /// needed it.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task WithoutTheBootstrapThereIsNothingToElectWithAsync(CancellationToken cancellationToken) {
    await using var connection = _connect();
    await connection.OpenAsync(cancellationToken);

    await Assert.That(await SchemaBootstrapPhase.CanElectAsync(connection, SCHEMA, cancellationToken))
      .IsFalse();

    await Assert.That(async () => await _scalarAsync<bool>(
      $"SELECT record_capability('{Guid.NewGuid()}'::uuid, 'migrator')"))
      .Throws<PostgresException>()
      .Because("the function a duty is recorded through is created by a migration, which is the "
        + "cycle the bootstrap breaks");
  }

  /// <summary>
  /// An instance that has not registered is refused a capability; one that has is not.
  /// </summary>
  /// <remarks>
  /// Why registration has to happen before the election rather than after it, proven against the
  /// real function. An earlier attempt elected first, read the resulting FALSE as "this instance may
  /// not migrate", and threw — which on any established database was every instance, because at
  /// this point in startup none of them has joined the registry yet.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task AnUnregisteredInstanceIsRefusedACapabilityAsync(CancellationToken cancellationToken) {
    await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    var stranger = new _Instance();
    await Assert.That(await _scalarAsync<bool>(
      $"SELECT record_capability('{stranger.InstanceId}'::uuid, 'migrator')")).IsFalse()
      .Because("there is no registry row to attach a holding to");

    await SchemaBootstrapPhase.RegisterInstanceAsync(
      _connect, SCHEMA, stranger, cancellationToken: cancellationToken);

    await Assert.That(await _scalarAsync<bool>(
      $"SELECT record_capability('{stranger.InstanceId}'::uuid, 'migrator')")).IsTrue()
      .Because("registering is the only thing that changed, and it is what makes election possible");
  }

  /// <summary>
  /// A tombstoned instance is still refused after the bootstrap.
  /// </summary>
  /// <remarks>
  /// The eviction fence has to survive being reached this early. The bootstrap brings up the
  /// tombstone table precisely so that <c>record_capability</c> can consult it, and an evicted
  /// instance must not be handed exclusive work just because it asked before the schema was
  /// migrated.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ATombstonedInstanceIsStillRefusedAsync(CancellationToken cancellationToken) {
    await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    var evicted = new _Instance();
    await SchemaBootstrapPhase.RegisterInstanceAsync(
      _connect, SCHEMA, evicted, cancellationToken: cancellationToken);

    await using (var db = _connect()) {
      await db.OpenAsync(cancellationToken);
      await using var tombstone = new NpgsqlCommand(
        "INSERT INTO wh_instance_evictions (instance_id, reason) VALUES ($1, 'test')", db);
      tombstone.Parameters.AddWithValue(evicted.InstanceId);
      await tombstone.ExecuteNonQueryAsync(cancellationToken);
    }

    await Assert.That(await _scalarAsync<bool>(
      $"SELECT record_capability('{evicted.InstanceId}'::uuid, 'migrator')")).IsFalse()
      .Because("the fence exists to keep exclusive work away from an instance that was reaped");
  }

  /// <summary>Running the bootstrap twice changes nothing and still reports ready.</summary>
  /// <remarks>
  /// Every instance runs this on every start, including restarts against a fully migrated database,
  /// so it has to be a no-op the second time and every time after.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task TheBootstrapIsIdempotentAsync(CancellationToken cancellationToken) {
    var logger = new _RecordingLogger();

    var first = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, logger, cancellationToken);
    var second = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, logger, cancellationToken);

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsTrue();
    await Assert.That(logger.Entries.Any(e => e.Level >= LogLevel.Warning)).IsFalse()
      .Because("a second run is the ordinary case, not something to report");
  }

  /// <summary>
  /// The bootstrap records nothing in the migration ledger.
  /// </summary>
  /// <remarks>
  /// A deliberate design property worth pinning, because breaking it would be subtle and bad. If
  /// the bootstrap wrote hash rows, the ordinary pass would read them, conclude those migrations
  /// were already applied, and skip work the bootstrap only partly did. Making objects exist and
  /// claiming to have migrated them are different things, and only the ordinary pass does the
  /// second.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task TheBootstrapRecordsNothingInTheLedgerAsync(CancellationToken cancellationToken) {
    await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_schema_migrations"))
      .IsEqualTo(0L)
      .Because("the ordinary pass owns the ledger; the bootstrap only makes objects exist");
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_schema_versions"))
      .IsEqualTo(0L);
  }

  /// <summary>A closure the database already holds is not applied again.</summary>
  /// <remarks>
  /// Every instance start used to re-run the closure's DDL, each statement taking a lock on a table
  /// the running instances write. An instance an autoscaler started under load deadlocked against
  /// the maintenance sweep and the poll sources within two seconds of starting. A closure that
  /// matches what the database recorded needs no statement and no lock.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ACurrentClosureIsNotAppliedAgainAsync(CancellationToken cancellationToken) {
    var scripts = _bootstrapScripts();
    scripts.Add(("marker",
      "CREATE TABLE IF NOT EXISTS bootstrap_marker (note TEXT NOT NULL); "
      + "INSERT INTO bootstrap_marker (note) VALUES ('applied');"));

    var first = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);
    var second = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsTrue();
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM bootstrap_marker"))
      .IsEqualTo(1L)
      .Because("the closure offered is the closure recorded; running its statements again takes DDL "
        + "locks on hot tables for nothing, which is what deadlocked a start under load");
  }

  /// <summary>A closure that changed is applied, and the record moves with it.</summary>
  [Test]
  [Timeout(120000)]
  public async Task AChangedClosureIsAppliedAsync(CancellationToken cancellationToken) {
    var scripts = _bootstrapScripts();
    scripts.Add(("marker",
      "CREATE TABLE IF NOT EXISTS bootstrap_marker (note TEXT NOT NULL); "
      + "INSERT INTO bootstrap_marker (note) VALUES ('first');"));
    await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    scripts.Add(("marker-2", "INSERT INTO bootstrap_marker (note) VALUES ('second');"));
    await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);
    await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM bootstrap_marker"))
      .IsEqualTo(3L)
      .Because("a new closure runs once in full (both markers) and then not again");
  }

  /// <summary>
  /// An instance that cannot take the lock applies nothing, and says so by what it reports.
  /// </summary>
  /// <remarks>
  /// The expected outcome for every instance but one. On an empty database it must report that no
  /// election is possible yet, because nothing has been created; reporting ready would send it off
  /// to elect against objects that are not there.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task AnInstanceThatCannotTakeTheLockAppliesNothingAsync(CancellationToken cancellationToken) {
    await using var holder = _connect();
    await holder.OpenAsync(cancellationToken);
    await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({LOCK_ID})", holder)) {
      await take.ExecuteScalarAsync(cancellationToken);
    }

    var ready = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    await Assert.That(ready).IsFalse()
      .Because("it applied nothing and nothing was there, so there is nothing to elect with yet");
    await Assert.That(await _scalarAsync<string>(
      "SELECT to_regclass('public.wh_service_instances')::text")).IsNull()
      .Because("a loser must not apply the bootstrap alongside the holder");

    await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({LOCK_ID})", holder);
    await release.ExecuteScalarAsync(cancellationToken);
  }

  /// <summary>
  /// An instance that cannot take the lock still reports ready when the holder already finished.
  /// </summary>
  /// <remarks>
  /// The other half, and the common case on a restart: the objects are there, this instance did not
  /// create them, and it can elect perfectly well. Reporting not-ready here would give up the
  /// election for no reason on every instance but one.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ALoserStillReportsReadyWhenTheObjectsAlreadyExistAsync(CancellationToken cancellationToken) {
    await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    await using var holder = _connect();
    await holder.OpenAsync(cancellationToken);
    await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({LOCK_ID})", holder)) {
      await take.ExecuteScalarAsync(cancellationToken);
    }

    var ready = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    await Assert.That(ready).IsTrue();

    await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({LOCK_ID})", holder);
    await release.ExecuteScalarAsync(cancellationToken);
  }

  /// <summary>
  /// A script that fails costs the election, not the startup.
  /// </summary>
  /// <remarks>
  /// The failure mode that decides whether this is safe to put in front of every startup. A
  /// bootstrap that threw would turn a wrong closure, a permission problem, or a half-created schema
  /// into a service that cannot start at all — strictly worse than the duplicated work an election
  /// avoids. It reports instead, and the caller migrates under the advisory lock.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task AFailedScriptReportsNotReadyRatherThanThrowingAsync(CancellationToken cancellationToken) {
    var logger = new _RecordingLogger();

    var ready = await SchemaBootstrapPhase.ApplyAsync(
      _connect,
      LOCK_ID,
      [("broken", "CREATE TABLE nonsense (x int) INHERITS (does_not_exist);")],
      SCHEMA,
      TIMEOUT_SECONDS,
      logger,
      cancellationToken);

    await Assert.That(ready).IsFalse();
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning)).IsTrue()
      .Because("losing the election silently would leave a fleet duplicating work with no clue why");
    await Assert.That(await _lockHoldersAsync()).IsEqualTo(0L)
      .Because("a lock held after a failure would stall every other instance's bootstrap too");
  }

  /// <summary>
  /// A failed script rolls the whole bootstrap back, leaving nothing half created.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The bootstrap runs as one transaction, so this is all-or-nothing by construction, and that is
  /// the behavior worth having. A half-applied bootstrap cannot elect anything anyway: the probe
  /// asks for the full set, so "some of it landed" and "none of it landed" lead to the same
  /// fallback. Leaving the partial objects behind would only make the next instance's failure
  /// harder to read.
  /// </para>
  /// <para>
  /// Nothing is lost by it either. The ordinary migration pass applies these same files under its
  /// own lock and records them, so the objects still arrive; only the election is given up.
  /// </para>
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task AFailedScriptRollsBackTheWholeBootstrapAsync(CancellationToken cancellationToken) {
    var scripts = new List<(string Name, string Sql)>(_bootstrapScripts()) {
      ("broken", "CREATE TABLE nonsense (x int) INHERITS (does_not_exist);"),
    };

    var logger = new _RecordingLogger();
    var ready = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, logger, cancellationToken);

    await Assert.That(ready).IsFalse();
    await Assert.That(await _scalarAsync<string>(
      "SELECT to_regclass('public.wh_service_instances')::text")).IsNull()
      .Because("the good scripts ran before the broken one and must have gone back with it");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning)).IsTrue()
      .Because("giving up the election silently would leave a fleet duplicating work with no clue why");
  }

  /// <summary>
  /// The lock is transaction scoped, so a failure cannot leave it held.
  /// </summary>
  /// <remarks>
  /// The reason it is not a session lock. A session lock does not survive a transaction-pooling
  /// front end, because the lock and the unlock land on different server connections; the unlock
  /// misses, and since both advisory scopes share one lock space the leaked lock would then block
  /// the DDL phase's own try-lock on the same key for ever. Nothing would migrate the schema again.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task AFailedBootstrapLeavesTheLockFreeForTheDdlPhaseAsync(CancellationToken cancellationToken) {
    await SchemaBootstrapPhase.ApplyAsync(
      _connect,
      LOCK_ID,
      [("broken", "CREATE TABLE nonsense (x int) INHERITS (does_not_exist);")],
      SCHEMA,
      TIMEOUT_SECONDS,
      new _RecordingLogger(),
      cancellationToken);

    await Assert.That(await _lockHoldersAsync()).IsEqualTo(0L);

    // The DDL phase takes the same key, transaction scoped. It has to be able to.
    await using var ddl = _connect();
    await ddl.OpenAsync(cancellationToken);
    await using var transaction = await ddl.BeginTransactionAsync(cancellationToken);
    await using var take = new NpgsqlCommand($"SELECT pg_try_advisory_xact_lock({LOCK_ID})", ddl);
    await Assert.That(await take.ExecuteScalarAsync(cancellationToken) is true).IsTrue()
      .Because("a lock the bootstrap leaked would stall every migration on this schema for ever");
    await transaction.CommitAsync(cancellationToken);
  }

  /// <summary>The lock is given back.</summary>
  [Test]
  [Timeout(120000)]
  public async Task TheBootstrapReleasesTheLockAsync(CancellationToken cancellationToken) {
    await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    await Assert.That(await _lockHoldersAsync()).IsEqualTo(0L);
  }

  /// <summary>Registering the same instance twice is harmless.</summary>
  /// <remarks>
  /// A restart reuses nothing, but a retried startup calls this again with the same identity, and
  /// the registration function's freshness guard means the second call is a no-op rather than a
  /// conflict.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task RegisteringTwiceIsHarmlessAsync(CancellationToken cancellationToken) {
    await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    var instance = new _Instance();
    await SchemaBootstrapPhase.RegisterInstanceAsync(
      _connect, SCHEMA, instance, cancellationToken: cancellationToken);
    await SchemaBootstrapPhase.RegisterInstanceAsync(
      _connect, SCHEMA, instance, cancellationToken: cancellationToken);

    await Assert.That(await _scalarAsync<long>(
      $"SELECT count(*) FROM wh_service_instances WHERE instance_id = '{instance.InstanceId}'"))
      .IsEqualTo(1L);
  }

  /// <summary>Missing arguments are caller errors.</summary>
  [Test]
  public async Task MissingArgumentsAreRefusedAsync() {
    await Assert.That(async () => await SchemaBootstrapPhase.ApplyAsync(
      null!, LOCK_ID, [], SCHEMA, TIMEOUT_SECONDS)).Throws<ArgumentNullException>();
    await Assert.That(async () => await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, null!, SCHEMA, TIMEOUT_SECONDS)).Throws<ArgumentNullException>();
    await Assert.That(async () => await SchemaBootstrapPhase.CanElectAsync(null!, SCHEMA))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await SchemaBootstrapPhase.RegisterInstanceAsync(
      _connect, SCHEMA, null!)).Throws<ArgumentNullException>();
  }

  private async Task<long> _lockHoldersAsync() =>
    await _scalarAsync<long>(
      "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND objsubid = 1 "
      + $"AND ((classid::bigint << 32) | (objid::bigint & 4294967295)) = {LOCK_ID}");
}
