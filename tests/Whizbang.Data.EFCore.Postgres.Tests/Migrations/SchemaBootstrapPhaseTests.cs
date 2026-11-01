// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
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
  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
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
  private sealed class RecordingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
    private sealed class Scope : IDisposable { public void Dispose() { } }
  }

  /// <summary>Signals the bootstrap's waits: the schema lock found held, and the first retry scheduled.</summary>
  private sealed class BootstrapSignals : ISchemaInitializationObserver {
    private readonly TaskCompletionSource _contended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<(int Attempt, Exception Exception, TimeSpan Delay)> _retry =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BootstrapSignals() =>
      Services = new ServiceCollection().AddSingleton<ISchemaInitializationObserver>(this).BuildServiceProvider();

    /// <summary>Where the bootstrap finds this observer.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Completes when the bootstrap finds the schema lock held and is about to wait for it.</summary>
    public Task Contended => _contended.Task;

    /// <summary>Completes when the bootstrap has scheduled its first retry.</summary>
    public Task<(int Attempt, Exception Exception, TimeSpan Delay)> RetryScheduled => _retry.Task;

    public ValueTask OnSchemaLockContendedAsync(string schema, CancellationToken cancellationToken) {
      _contended.TrySetResult();
      return ValueTask.CompletedTask;
    }

    public ValueTask OnBootstrapRetryScheduledAsync(
        string schema, int attempt, Exception exception, TimeSpan delay, CancellationToken cancellationToken) {
      _retry.TrySetResult((attempt, exception, delay));
      return ValueTask.CompletedTask;
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    var database = await PerTestDatabaseFactory.CreateAsync("bootstrap");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is null) {
      return;
    }

    await PerTestDatabaseFactory.DropAsync(_databaseName);
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

    var instance = new Instance();
    await SchemaBootstrapPhase.RegisterInstanceAsync(
      _connect, SCHEMA, instance, cancellationToken: cancellationToken);

    var recorded = await _scalarAsync<bool>(
      $"SELECT record_capability('{instance.InstanceId}'::uuid, 'migrator')");
    await Assert.That(recorded).IsTrue()
      .Because("this is the exact call the elector makes, and the one that could not be made "
        + "before the schema was migrated");
  }

  /// <summary>
  /// A context with no schema configured targets <c>public</c>, so the bootstrap finds the objects it
  /// created there rather than probing a schema whose name is empty.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task AnEmptySchemaNameTargetsThePublicSchemaAsync(CancellationToken cancellationToken) {
    var ready = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, _bootstrapScripts(), string.Empty, TIMEOUT_SECONDS, null, cancellationToken);

    await Assert.That(ready).IsTrue()
      .Because("the scripts run in public, and the probe has to look there to see what they created");
  }

  /// <summary>
  /// A named schema's bootstrap runs with that schema as <c>current_schema()</c>, so the migration text
  /// it carries answers about the schema it alters.
  /// </summary>
  /// <remarks>
  /// The bootstrap regions are shipped migration text, and shipped guards compare
  /// <c>table_schema = current_schema()</c>. The connection here keeps the default <c>search_path</c>,
  /// where that is <c>public</c>.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ANamedSchemasBootstrapRunsInThatSchemaAsync(CancellationToken cancellationToken) {
    const string named = NamedSchemaDbContext.SCHEMA;
    await _scalarAsync<object>($"CREATE SCHEMA IF NOT EXISTS {named}");
    var scripts = NamedSchemaDbContextSchemaExtensions.GetBootstrapScripts();
    scripts.Add(("probe", $"CREATE TABLE {named}.bootstrap_schema_probe AS SELECT current_schema()::TEXT AS seen"));

    await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, named, TIMEOUT_SECONDS, null, cancellationToken);

    await Assert.That(await _scalarAsync<string>($"SELECT seen FROM {named}.bootstrap_schema_probe"))
      .IsEqualTo(named)
      .Because("a shipped guard asking current_schema() must be asking about the schema it migrates");
    await Assert.That(await _scalarAsync<string>("SELECT current_schema()")).IsEqualTo("public")
      .Because("precondition: the setting belonged to the bootstrap's transaction, not to the database");
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

    var stranger = new Instance();
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

    var evicted = new Instance();
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
    var logger = new RecordingLogger();

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

  /// <summary>
  /// A closure whose scripts differ only in comment lines is the recorded closure.
  /// </summary>
  /// <remarks>
  /// Two instances of one release build their scripts independently. A header line a builder
  /// writes, or a clock stamp, is not a statement; if it took part in the hash, no second instance
  /// would ever find the record, and every start would apply the DDL under the lock, which is the
  /// deadlock the record exists to prevent.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task AClosureThatDiffersOnlyInCommentsIsCurrentAsync(CancellationToken cancellationToken) {
    var scripts = _bootstrapScripts();
    scripts.Add(("marker",
      "CREATE TABLE IF NOT EXISTS bootstrap_marker (note TEXT NOT NULL); "
      + "INSERT INTO bootstrap_marker (note) VALUES ('applied');"));
    await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    var commented = scripts.ConvertAll(
      s => (s.Name, "-- Generated: 2026-01-02 03:04:05 UTC\n" + s.Sql + "\n-- a note that changes no statement"));
    var again = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, commented, SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    await Assert.That(again).IsTrue();
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM bootstrap_marker"))
      .IsEqualTo(1L)
      .Because("comment lines carry no statement, so a closure that differs only there is the one already recorded");
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
  /// An instance that finds the schema lock held waits for it, then applies the closure (#1636): it does not skip.
  /// </summary>
  /// <remarks>
  /// Before #1636 a loser applied nothing and reported whatever it found, so on an empty database it gave up the
  /// election and every instance but one migrated under the lock alone. The bootstrap is Preflight's work and every
  /// instance needs its objects before it registers, so it waits its turn instead. Nothing is applied while another
  /// session holds the lock.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ABootstrapThatFindsTheLockHeld_WaitsForIt_ThenAppliesTheClosureAsync(CancellationToken cancellationToken) {
    await using var holder = _connect();
    await holder.OpenAsync(cancellationToken);
    await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({LOCK_ID})", holder)) {
      await take.ExecuteScalarAsync(cancellationToken);
    }
    var signals = new BootstrapSignals();

    var applying = SchemaBootstrapPhase.ApplyAsync(
      new SchemaBootstrapRequest(_connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS), signals.Services, new FakeTimeProvider(), null,
      cancellationToken);

    await Assert.That(await Task.WhenAny(signals.Contended, applying) == signals.Contended).IsTrue()
      .Because("an instance that finds the schema lock held waits for it; it does not return having applied nothing");
    await Assert.That(await _scalarAsync<string>("SELECT to_regclass('public.wh_service_instances')::text")).IsNull()
      .Because("nothing is applied while another session holds the schema lock");

    await using (var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({LOCK_ID})", holder)) {
      await release.ExecuteScalarAsync(cancellationToken);
    }

    await Assert.That(await applying).IsTrue().Because("once the lock is free the waiter applies the closure itself");
    await Assert.That(await _scalarAsync<string>("SELECT to_regclass('public.wh_service_instances')::text")).IsNotNull();
  }

  /// <summary>
  /// A waiter whose lock holder applied the same closure meanwhile applies nothing once it gets the lock: the closure
  /// record is read again under the lock.
  /// </summary>
  /// <remarks>
  /// The common shape of a fleet starting together on a new release: one instance applies, the others queue behind it,
  /// and each must find the holder's commit rather than run every statement again on tables that are now in use.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task AWaiterWhoseHolderAppliedTheClosureMeanwhile_AppliesNothingAsync(CancellationToken cancellationToken) {
    var scripts = _scriptsWithMarker();
    await using var holder = _connect();
    await holder.OpenAsync(cancellationToken);
    await using var transaction = await holder.BeginTransactionAsync(cancellationToken);
    await using (var take = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({LOCK_ID})", holder)) {
      await take.ExecuteScalarAsync(cancellationToken);
    }
    foreach (var (_, sql) in scripts) {
      await using var apply = new NpgsqlCommand(sql, holder);
      await apply.ExecuteNonQueryAsync(cancellationToken);
    }
    await using (var record = new NpgsqlCommand("INSERT INTO wh_bootstrap_closure (closure_hash) VALUES ($1)", holder)) {
      record.Parameters.AddWithValue(SchemaBootstrapPhase.ClosureHash(scripts));
      await record.ExecuteNonQueryAsync(cancellationToken);
    }
    var signals = new BootstrapSignals();

    var applying = SchemaBootstrapPhase.ApplyAsync(
      new SchemaBootstrapRequest(_connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS), signals.Services, new FakeTimeProvider(), null, cancellationToken);

    await Assert.That(await Task.WhenAny(signals.Contended, applying) == signals.Contended).IsTrue()
      .Because("the holder has not committed, so the waiter has to wait for it");
    await transaction.CommitAsync(cancellationToken);

    await Assert.That(await applying).IsTrue();
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM bootstrap_marker")).IsEqualTo(1L)
      .Because("the holder's commit applied this closure, which the waiter finds once it has the lock");
  }

  /// <summary>A bootstrap canceled while it waits for the schema lock ends with the cancellation and applies nothing.</summary>
  [Test]
  [Timeout(120000)]
  public async Task ABootstrapCanceledWhileItWaits_EndsWithTheCancellation_AndAppliesNothingAsync(CancellationToken cancellationToken) {
    await using var holder = _connect();
    await holder.OpenAsync(cancellationToken);
    await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({LOCK_ID})", holder)) {
      await take.ExecuteScalarAsync(cancellationToken);
    }
    var signals = new BootstrapSignals();
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var applying = SchemaBootstrapPhase.ApplyAsync(
      new SchemaBootstrapRequest(_connect, LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS), signals.Services, new FakeTimeProvider(), null,
      stop.Token);
    await Assert.That(await Task.WhenAny(signals.Contended, applying) == signals.Contended).IsTrue();
    await stop.CancelAsync();

    await Assert.That(async () => await applying).Throws<OperationCanceledException>();
    await Assert.That(await _scalarAsync<string>("SELECT to_regclass('public.wh_service_instances')::text")).IsNull();

    await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({LOCK_ID})", holder);
    await release.ExecuteScalarAsync(cancellationToken);
  }

  /// <summary>
  /// A transient database failure is retried once its delay has passed on the initializer's clock, and the closure
  /// lands (#1636). The failure is said at Warning with the script and the SQLSTATE.
  /// </summary>
  /// <remarks>
  /// The injected failure is a deadlock raised by the closure's last script on its first run only: a sequence counts the
  /// runs, and a sequence is not rolled back with the transaction that advanced it.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ATransientFailure_IsRetriedOnceItsDelayHasPassed_ThenTheClosureLandsAsync(CancellationToken cancellationToken) {
    await _scalarAsync<object>("CREATE SEQUENCE bootstrap_fails_once");
    var scripts = _bootstrapScripts();
    scripts.Add(("fails-once",
      "DO $$ BEGIN IF nextval('bootstrap_fails_once') = 1 THEN "
      + "RAISE EXCEPTION 'injected deadlock' USING ERRCODE = '40P01'; END IF; END $$;"));
    var signals = new BootstrapSignals();
    var time = new FakeTimeProvider();
    var logger = new RecordingLogger();

    var applying = SchemaBootstrapPhase.ApplyAsync(
      new SchemaBootstrapRequest(_connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS), signals.Services, time, logger, cancellationToken);

    await Assert.That(await Task.WhenAny(signals.RetryScheduled, applying) == signals.RetryScheduled).IsTrue()
      .Because("a transient failure is retried; it is not reported as a lost election");
    var (attempt, exception, delay) = await signals.RetryScheduled;
    await Assert.That(attempt).IsEqualTo(1);
    await Assert.That(exception is PostgresException { SqlState: "40P01" }).IsTrue();
    await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(applying.IsCompleted).IsFalse().Because("the retry waits for its delay on the initializer's clock");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning
        && e.Message.Contains("fails-once", StringComparison.Ordinal) && e.Message.Contains("40P01", StringComparison.Ordinal)))
      .IsTrue().Because("a retried failure is said out loud, with what failed and why");

    time.Advance(delay);

    await Assert.That(await applying).IsTrue();
    await Assert.That(await _scalarAsync<long>("SELECT last_value FROM bootstrap_fails_once")).IsEqualTo(2L)
      .Because("the closure ran twice: the failed attempt and the retry that landed");
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
  /// A transient failure the server never answered (a refused connection carries no SQLSTATE) is retried like any
  /// other, and its log line says <c>SQLSTATE none</c> rather than leaving the field blank.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task ATransientFailureWithNoSqlState_IsRetried_AndLoggedAsNoneAsync(CancellationToken cancellationToken) {
    // Nothing listens on port 1, so the connection is refused at once: a dropped-connection failure with no SQLSTATE.
    var refused = new NpgsqlConnectionStringBuilder(_connectionString) { Host = "127.0.0.1", Port = 1, Timeout = 5 }.ConnectionString;
    var signals = new BootstrapSignals();
    var logger = new RecordingLogger();
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var applying = SchemaBootstrapPhase.ApplyAsync(
      new SchemaBootstrapRequest(() => new NpgsqlConnection(refused), LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS),
      signals.Services, new FakeTimeProvider(), logger, stop.Token);

    await Assert.That(await Task.WhenAny(signals.RetryScheduled, applying) == signals.RetryScheduled).IsTrue()
      .Because("a refused connection is transient, so it is retried");
    var (attempt, exception, _) = await signals.RetryScheduled;
    await Assert.That(attempt).IsEqualTo(1);
    await Assert.That(exception is NpgsqlException { SqlState: null }).IsTrue();
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning
        && e.Message.Contains("<connect>", StringComparison.Ordinal) && e.Message.Contains("SQLSTATE none", StringComparison.Ordinal)))
      .IsTrue().Because("a failure with no SQLSTATE is logged as none, with the step that failed");

    await stop.CancelAsync();
    await Assert.That(async () => await applying).Throws<OperationCanceledException>()
      .Because("the retry's wait ends with the start's cancellation");
  }

  /// <summary>A missing request is a caller error.</summary>
  [Test]
  public async Task AMissingRequest_IsRefusedAsync() =>
    await Assert.That(async () => await SchemaBootstrapPhase.ApplyAsync(
      null!, null, TimeProvider.System, null, CancellationToken.None)).Throws<ArgumentNullException>();

  /// <summary>
  /// A failure that is not transient fails startup loudly (#1636): it is thrown as a
  /// <see cref="SchemaBootstrapException"/> naming the script, logged at Error, and not retried.
  /// </summary>
  /// <remarks>
  /// Before #1636 it was logged at Warning and the instance migrated under the lock alone, so a wrong closure or a
  /// missing permission surfaced only as an election that never happened. The same failure would recur on every
  /// attempt; retrying it would only hide it.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ANonTransientFailure_FailsStartupLoudly_NamingWhatFailedAsync(CancellationToken cancellationToken) {
    var logger = new RecordingLogger();

    var thrown = await Assert.That(async () => await SchemaBootstrapPhase.ApplyAsync(
      _connect,
      LOCK_ID,
      [("broken", "CREATE TABLE nonsense (x int) INHERITS (does_not_exist);")],
      SCHEMA,
      TIMEOUT_SECONDS,
      logger,
      cancellationToken)).Throws<SchemaBootstrapException>();

    await Assert.That(thrown!.Step).IsEqualTo("broken");
    await Assert.That(thrown.Schema).IsEqualTo(SCHEMA);
    await Assert.That(thrown.InnerException is PostgresException { SqlState: "42P01" }).IsTrue();
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("broken", StringComparison.Ordinal)))
      .IsTrue().Because("a failure that stops startup says what failed, at Error");
    await Assert.That(await _lockHoldersAsync()).IsEqualTo(0L)
      .Because("a lock held after a failure would stall every other instance's bootstrap too");
  }

  /// <summary>
  /// A database that cannot be connected to for a reason that is not transient fails the bootstrap naming the
  /// connection as what failed.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task AConnectionRefusedForGood_FailsTheBootstrap_NamingTheConnectionAsync(CancellationToken cancellationToken) {
    var missing = new NpgsqlConnectionStringBuilder(_connectionString) { Database = _databaseName + "_missing" }.ConnectionString;

    var thrown = await Assert.That(async () => await SchemaBootstrapPhase.ApplyAsync(
      () => new NpgsqlConnection(missing), LOCK_ID, _bootstrapScripts(), SCHEMA, TIMEOUT_SECONDS, null, cancellationToken))
      .Throws<SchemaBootstrapException>();

    await Assert.That(thrown!.Step).IsEqualTo("<connect>");
    await Assert.That(thrown.InnerException is PostgresException { SqlState: "3D000" }).IsTrue();
  }

  /// <summary>
  /// A failed script rolls the whole bootstrap back, leaving nothing half created, and fails the start.
  /// </summary>
  /// <remarks>
  /// The bootstrap runs as one transaction, so this is all-or-nothing by construction: a partly applied closure cannot
  /// elect anything, and leaving its objects behind would only make the next attempt's failure harder to read.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task AFailedScriptRollsBackTheWholeBootstrapAsync(CancellationToken cancellationToken) {
    var scripts = new List<(string Name, string Sql)>(_bootstrapScripts()) {
      ("broken", "CREATE TABLE nonsense (x int) INHERITS (does_not_exist);"),
    };

    var logger = new RecordingLogger();
    await Assert.That(async () => await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, logger, cancellationToken)).Throws<SchemaBootstrapException>();

    await Assert.That(await _scalarAsync<string>(
      "SELECT to_regclass('public.wh_service_instances')::text")).IsNull()
      .Because("the good scripts ran before the broken one and must have gone back with it");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Error)).IsTrue()
      .Because("a bootstrap that stops startup says so at Error");
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
    await Assert.That(async () => await SchemaBootstrapPhase.ApplyAsync(
      _connect,
      LOCK_ID,
      [("broken", "CREATE TABLE nonsense (x int) INHERITS (does_not_exist);")],
      SCHEMA,
      TIMEOUT_SECONDS,
      new RecordingLogger(),
      cancellationToken)).Throws<SchemaBootstrapException>();

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

    var instance = new Instance();
    await SchemaBootstrapPhase.RegisterInstanceAsync(
      _connect, SCHEMA, instance, cancellationToken: cancellationToken);
    await SchemaBootstrapPhase.RegisterInstanceAsync(
      _connect, SCHEMA, instance, cancellationToken: cancellationToken);

    await Assert.That(await _scalarAsync<long>(
      $"SELECT count(*) FROM wh_service_instances WHERE instance_id = '{instance.InstanceId}'"))
      .IsEqualTo(1L);
  }

  /// <summary>The bootstrap scripts with a marker whose legacy closure hash differs from its normalized one.</summary>
  private static List<(string Name, string Sql)> _scriptsWithMarker() {
    var scripts = _bootstrapScripts();
    scripts.Add(("marker",
      "CREATE TABLE IF NOT EXISTS bootstrap_marker (note TEXT NOT NULL); -- a comment on a statement's line\n"
      + "INSERT INTO bootstrap_marker (note) VALUES ('applied');"));
    return scripts;
  }

  /// <summary>Stands a database where an earlier release recorded <paramref name="scripts"/> under the legacy hash.</summary>
  private async Task _recordUnderTheLegacyHashAsync(List<(string Name, string Sql)> scripts, CancellationToken cancellationToken) {
    await SchemaBootstrapPhase.ApplyAsync(_connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);
    await using var db = _connect();
    await db.OpenAsync(cancellationToken);
    await using var legacy = new NpgsqlCommand("UPDATE wh_bootstrap_closure SET closure_hash = $1", db);
    legacy.Parameters.AddWithValue(SchemaBootstrapPhase.LegacyClosureHash(scripts));
    await legacy.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<bool> _isRecordedAsync(string hash) =>
    await _scalarAsync<long>($"SELECT count(*) FROM wh_bootstrap_closure WHERE closure_hash = '{hash}'") == 1L;

  /// <summary>
  /// A closure an earlier release recorded under its legacy hash is current: nothing is applied, and the normalized
  /// hash is recorded beside it, so the next start finds it directly.
  /// </summary>
  /// <remarks>
  /// The upgrade path. Without it every database would re-run the bootstrap DDL on its first start after the hash
  /// became normalized, which is the per-statement lock on hot tables the record exists to avoid. The legacy row is
  /// kept: an instance of the earlier release still running in the fleet keeps finding its own record.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task AClosureRecordedUnderItsLegacyHashIsCurrentAndRecordedAgainAsync(CancellationToken cancellationToken) {
    var scripts = _scriptsWithMarker();
    await _recordUnderTheLegacyHashAsync(scripts, cancellationToken);
    await Assert.That(await _isRecordedAsync(SchemaBootstrapPhase.ClosureHash(scripts))).IsFalse()
      .Because("precondition: only the legacy hash is recorded");

    var logger = new RecordingLogger();
    var ready = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, logger, cancellationToken);

    await Assert.That(ready).IsTrue();
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM bootstrap_marker")).IsEqualTo(1L)
      .Because("the database holds exactly this closure, recorded under the hash an earlier release computed");
    await Assert.That(await _isRecordedAsync(SchemaBootstrapPhase.ClosureHash(scripts))).IsTrue();
    await Assert.That(await _isRecordedAsync(SchemaBootstrapPhase.LegacyClosureHash(scripts))).IsTrue()
      .Because("an instance of the earlier release still in the fleet keeps finding its own record");
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("legacy", StringComparison.Ordinal))).IsTrue();

    var second = new RecordingLogger();
    await SchemaBootstrapPhase.ApplyAsync(_connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, second, cancellationToken);
    await Assert.That(second.Entries.Any(e => e.Message.Contains("legacy", StringComparison.Ordinal))).IsFalse()
      .Because("the next start finds the normalized hash and has nothing to record again");
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM bootstrap_marker")).IsEqualTo(1L);
  }

  /// <summary>
  /// An instance that finds the legacy record while another holds the lock applies nothing and records nothing; the
  /// closure is still current.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task ALegacyRecordFoundWhileTheLockIsHeldIsCurrentAndLeftAsync(CancellationToken cancellationToken) {
    var scripts = _scriptsWithMarker();
    await _recordUnderTheLegacyHashAsync(scripts, cancellationToken);

    await using var holder = _connect();
    await holder.OpenAsync(cancellationToken);
    await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({LOCK_ID})", holder)) {
      await take.ExecuteScalarAsync(cancellationToken);
    }

    var ready = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, null, cancellationToken);

    await Assert.That(ready).IsTrue();
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM bootstrap_marker")).IsEqualTo(1L);
    await Assert.That(await _isRecordedAsync(SchemaBootstrapPhase.ClosureHash(scripts))).IsFalse()
      .Because("the holder of the lock records it; this instance only reads");

    await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({LOCK_ID})", holder);
    await release.ExecuteScalarAsync(cancellationToken);
  }

  /// <summary>
  /// Recording the normalized hash beside a legacy record is not allowed to fail the start: the closure is current
  /// either way, and the next start tries again.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task ALegacyRecordThatCannotBeRecordedAgainIsReportedNotThrownAsync(CancellationToken cancellationToken) {
    var scripts = _scriptsWithMarker();
    await _recordUnderTheLegacyHashAsync(scripts, cancellationToken);
    await using (var db = _connect()) {
      await db.OpenAsync(cancellationToken);
      await using var refuse = new NpgsqlCommand("""
        CREATE FUNCTION refuse_closure_insert() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'closure record refused'; END $$;
        CREATE TRIGGER refuse_closure_insert BEFORE INSERT ON wh_bootstrap_closure
          FOR EACH ROW EXECUTE FUNCTION refuse_closure_insert();
        """, db);
      await refuse.ExecuteNonQueryAsync(cancellationToken);
    }

    var logger = new RecordingLogger();
    var ready = await SchemaBootstrapPhase.ApplyAsync(
      _connect, LOCK_ID, scripts, SCHEMA, TIMEOUT_SECONDS, logger, cancellationToken);

    await Assert.That(ready).IsTrue();
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM bootstrap_marker")).IsEqualTo(1L);
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("legacy", StringComparison.Ordinal)))
      .IsTrue().Because("a record that could not be written is said out loud, with what it was");
    await Assert.That(await _lockHoldersAsync()).IsEqualTo(0L);
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
      + $"AND ((classid::bigint << 32) | (objid::bigint & 4294967295)) = {LOCK_ID} "
      // pg_locks is cluster-wide; a sibling fixture's database on the shared container takes the same key.
      + "AND database = (SELECT oid FROM pg_database WHERE datname = current_database())");
}
