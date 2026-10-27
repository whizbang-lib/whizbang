// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1752: the partition maintenance cycle under <c>Whizbang:Workers:Maintenance:Partitioning:Strategy</c>. Where
/// the strategy resolves to pg_partman, each table gets its pg_partman pass (repeated while the oldest partition is
/// being re-bounded, at most three steps) before the built-in pass, which still drops expired partitions and creates
/// whatever pg_partman did not, saying so. <c>Auto</c> keeps the built-in manager where pg_partman is not Ready and says
/// why once, and falls back for the cycle when a pass fails; an explicit <c>PgPartman</c> fails the cycle instead.
/// </summary>
/// <remarks>
/// The test server has no pg_partman. Where a test needs a pass that succeeds, it replaces
/// <c>wh_partition_partman</c> in its own database with a stand-in that records each call and answers as the real
/// pass would; the real pass is covered by <c>PgPartmanPartitionSqlTests</c>. Every cycle runs at a fixed moment.
/// </remarks>
/// <docs>operations/infrastructure/partitioned-ledgers#pg-partman</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PartitionMaintenance.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PartitionStrategyAnnouncer.cs</code-under-test>
[NotInParallel("PostgreSQL")]
[Category("Integration")]
public class PgPartmanPartitionMaintenanceTests : PostgresTestBase {
  private const string TABLE = "wh_partition_probe";
  private const string STRATEGY_KEY = "Whizbang:Workers:Maintenance:Partitioning:Strategy";
  private static readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
  private static readonly PartitionedTable _probe = PartitionedTable.RetainedIndefinitely(TABLE, "at", TimeSpan.FromDays(1));

  private readonly CapturingLogger<DapperPartitionMaintenanceStep> _logger = new();

  private DapperPartitionMaintenanceStep _step() => new(ConnectionString, _logger, new FixedClock(_now));

  private static async Task<ServiceProvider> _servicesAsync(
      PartitionManagementStrategy strategy, CapabilityState? pgPartman, bool announcer = true, PartitionedTable? table = null) {
    var services = new ServiceCollection();
    services.AddSingleton(Options.Create(new MaintenanceWorkerOptions {
      Partitioning = new MaintenancePartitioningOptions { Enabled = true, CreateAhead = 2, Strategy = strategy },
    }));
    services.AddPartitionedTable(table ?? _probe);
    if (announcer) {
      services.AddSingleton(new PartitionStrategyAnnouncer());
    }
    if (pgPartman is { } state) {
      var registry = new CapabilityRegistry([new PgPartmanProvider(state)], NullLogger<CapabilityRegistry>.Instance);
      await registry.RefreshAsync(CancellationToken.None);
      services.AddSingleton<ICapabilityRegistry>(registry);
    }
    return services.BuildServiceProvider();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  [Before(Test)]
  public async Task CreateTheProbeAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(
      $"CREATE TABLE {TABLE} (id BIGSERIAL, at TIMESTAMPTZ NOT NULL, PRIMARY KEY (id, at)) PARTITION BY RANGE (at)");
  }

  // A stand-in for the pass: records each call, answers 'rebounding' for the first <reboundingCalls> calls, then raises
  // the WARNING pg_partman raises when it skips a set and reports the table rebounded, registered and maintained. It
  // creates no partition, as pg_partman creates none when it skips a set.
  private async Task _standInPassAsync(int reboundingCalls) {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync($"""
      CREATE TABLE partman_calls (n SERIAL, table_name TEXT, ahead INT, partman_schema TEXT);
      CREATE OR REPLACE FUNCTION public.wh_partition_partman(
        p_table TEXT, p_interval INTERVAL, p_ahead INTEGER, p_partman_schema TEXT DEFAULT NULL, p_now TIMESTAMPTZ DEFAULT NOW())
      RETURNS TABLE(action TEXT, partition_name TEXT) AS $$
      DECLARE v_calls INTEGER;
      BEGIN
        INSERT INTO partman_calls (table_name, ahead, partman_schema) VALUES (p_table, p_ahead, p_partman_schema);
        SELECT count(*) INTO v_calls FROM partman_calls;
        IF v_calls <= {reboundingCalls} THEN
          RETURN QUERY SELECT 'rebounding'::TEXT, p_table;
          RETURN;
        END IF;
        RAISE WARNING 'pg_partman maintenance skipped partition set for parent table public.%', p_table;
        RETURN QUERY SELECT 'rebounded'::TEXT, p_table;
        RETURN QUERY SELECT 'registered'::TEXT, p_table;
        RETURN QUERY SELECT 'maintained'::TEXT, p_table;
      END;
      $$ LANGUAGE plpgsql;
      """);
  }

  private async Task<long> _passCallsAsync() {
    await using var connection = await _openAsync();
    return await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM partman_calls");
  }

  private async Task<List<string>> _partitionsAsync() {
    await using var connection = await _openAsync();
    return [.. await connection.QueryAsync<string>(
      $"SELECT c.relname::text FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = '{TABLE}'::regclass ORDER BY 1")];
  }

  private List<string> _logged(LogLevel level) => [.. _logger.Entries.Where(e => e.Level == level).Select(e => e.Message ?? "")];

  [Test]
  public async Task Auto_WithPgPartmanReady_RunsItsPassUntilTheReboundIsDone_ThenTheBuiltInPassAsync() {
    await _standInPassAsync(reboundingCalls: 2);
    await using var services = await _servicesAsync(PartitionManagementStrategy.Auto, CapabilityState.Ready);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _passCallsAsync()).IsEqualTo(3L)
      .Because("each re-bound step commits on its own, so the pass is repeated until it reports something else");
    await using (var connection = await _openAsync()) {
      var call = await connection.QuerySingleAsync<(int Ahead, string? Schema)>("SELECT ahead, partman_schema FROM partman_calls WHERE n = 3");
      await Assert.That(call.Ahead).IsEqualTo(2).Because("pg_partman makes as many partitions ahead as the option asks");
      await Assert.That(call.Schema).IsNull().Because("the pass finds pg_partman's schema itself");
    }
    await Assert.That(await _partitionsAsync())
      .IsEquivalentTo([TABLE + "_default", TABLE + "_p20261021", TABLE + "_p20261022", TABLE + "_p20261023"])
      .Because("the built-in pass still runs, and creates whatever pg_partman did not");
    var warnings = _logged(LogLevel.Warning);
    await Assert.That(warnings).Contains($"pg_partman did not create {TABLE}_p20261021 of {TABLE}; the built-in manager created it");
    await Assert.That(warnings.Count(w => w.StartsWith("pg_partman did not create", StringComparison.Ordinal))).IsEqualTo(3)
      .Because("the default partition is always the built-in manager's, so only the three ranged ones are pg_partman's misses");
    await Assert.That(warnings).Contains($"pg_partman reported for {TABLE}: pg_partman maintenance skipped partition set for parent table public.{TABLE}")
      .Because("pg_partman reports a set it skipped only as a WARNING, which would otherwise go unseen");
    await Assert.That(_logged(LogLevel.Information)).Contains(
      "Partition management: Auto: pg_partman is Ready, so pg_partman creates partitions ahead; the built-in manager still drops expired ones");
  }

  [Test]
  public async Task AReboundThatNeverCompletes_IsLeftAfterThreeStepsAsync() {
    await _standInPassAsync(reboundingCalls: int.MaxValue);
    await using var services = await _servicesAsync(PartitionManagementStrategy.PgPartman, CapabilityState.Ready);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _passCallsAsync()).IsEqualTo(3L).Because("a re-bound takes three steps; a fourth means something is wrong");
    await Assert.That(_logged(LogLevel.Warning).Where(w => w.StartsWith("pg_partman did not create", StringComparison.Ordinal))).IsEmpty()
      .Because("until pg_partman maintains the table, the built-in manager creating its partitions is not a miss");
  }

  [Test]
  public async Task WithPgPartman_APartitionTheBuiltInManagerDrops_IsNotReportedAsAMissAsync() {
    await _standInPassAsync(reboundingCalls: 0);
    await using (var connection = await _openAsync()) {
      await connection.ExecuteAsync(
        $"CREATE TABLE {TABLE}_p20261001 PARTITION OF {TABLE} FOR VALUES FROM ('2026-10-01') TO ('2026-10-02')");
    }
    var retained = PartitionedTable.RetainedFor(TABLE, "at", TimeSpan.FromDays(1), TimeSpan.FromDays(7));
    await using var services = await _servicesAsync(PartitionManagementStrategy.PgPartman, CapabilityState.Ready, table: retained);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _partitionsAsync()).DoesNotContain(TABLE + "_p20261001")
      .Because("with pg_partman creating partitions, the built-in manager still drops the expired ones");
    var misses = _logged(LogLevel.Warning).Where(w => w.StartsWith("pg_partman did not create", StringComparison.Ordinal)).ToList();
    await Assert.That(misses.Count).IsEqualTo(3).Because("only the three ranged partitions the built-in pass created are misses");
    await Assert.That(misses.Any(w => w.Contains("_p20261001", StringComparison.Ordinal))).IsFalse()
      .Because("a dropped partition is the built-in manager's own work, not something pg_partman failed to create");
  }

  [Test]
  public async Task Auto_WhenPgPartmanIsNotPermitted_KeepsTheBuiltInManager_AndSaysSoOnceAsync() {
    await _standInPassAsync(reboundingCalls: 0);
    await using var services = await _servicesAsync(PartitionManagementStrategy.Auto, CapabilityState.NotPermitted);

    await _step().RunAsync(services, CancellationToken.None);
    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _passCallsAsync()).IsEqualTo(0L);
    await Assert.That(await _partitionsAsync()).Contains(TABLE + "_p20261021");
    await Assert.That(_logged(LogLevel.Information).Count(m =>
      m == "Partition management: Auto: pg_partman is NotPermitted, so the built-in manager creates and drops partitions"))
      .IsEqualTo(1).Because("the choice is said when it is first made and when it changes, not every cycle");
  }

  [Test]
  public async Task WithoutTheAnnouncer_TheChoiceIsSaidEveryCycleAsync() {
    await using var services = await _servicesAsync(PartitionManagementStrategy.BuiltIn, pgPartman: null, announcer: false);

    await _step().RunAsync(services, CancellationToken.None);
    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(_logged(LogLevel.Information).Count(m =>
      m == "Partition management: BuiltIn: the built-in manager creates and drops partitions")).IsEqualTo(2);
  }

  [Test]
  public async Task BuiltIn_NeverRunsAPgPartmanPass_EvenWhereItIsReadyAsync() {
    await _standInPassAsync(reboundingCalls: 0);
    await using var services = await _servicesAsync(PartitionManagementStrategy.BuiltIn, CapabilityState.Ready);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _passCallsAsync()).IsEqualTo(0L);
    await Assert.That(await _partitionsAsync()).Contains(TABLE + "_p20261023");
  }

  [Test]
  public async Task ExplicitPgPartman_WithoutTheExtension_FailsTheCycleNamingTheSettingAsync() {
    await using var services = await _servicesAsync(PartitionManagementStrategy.PgPartman, pgPartman: null);

    var error = await Assert.That(() => _step().RunAsync(services, CancellationToken.None)).Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.UndefinedObject);
    await Assert.That(error.MessageText).Contains(STRATEGY_KEY);
  }

  [Test]
  public async Task Auto_WhenThePgPartmanPassFails_ReportsIt_AndTheBuiltInManagerKeepsTheTableAsync() {
    // The registry says Ready, but this database has no pg_partman: the real pass raises.
    await using var services = await _servicesAsync(PartitionManagementStrategy.Auto, CapabilityState.Ready);

    await _step().RunAsync(services, CancellationToken.None);

    var failure = _logger.Entries.Single(e => e.Level == LogLevel.Warning && e.Exception is PostgresException);
    await Assert.That(failure.Message).IsEqualTo(
      $"pg_partman pass failed for {TABLE}; the built-in manager keeps it this cycle ({STRATEGY_KEY} = Auto)");
    await Assert.That(await _partitionsAsync()).Contains(TABLE + "_p20261023");
    await Assert.That(_logged(LogLevel.Warning).Where(w => w.StartsWith("pg_partman did not create", StringComparison.Ordinal))).IsEmpty();
  }

  [Test]
  public async Task TheFrameworkTables_RegisterOneAnnouncerAsync() {
    var services = new ServiceCollection();
    services.AddFrameworkPartitionedTables();
    services.AddFrameworkPartitionedTables();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetServices<PartitionStrategyAnnouncer>().Count()).IsEqualTo(1);
    await Assert.That(provider.GetService<PartitionStrategyAnnouncer>()).IsSameReferenceAs(provider.GetService<PartitionStrategyAnnouncer>());
  }

  [Test]
  public async Task TheAnnouncer_SaysAReasonWhenItIsNewOrChangedAsync() {
    var announcer = new PartitionStrategyAnnouncer();

    await Assert.That(announcer.IsNew("a")).IsTrue();
    await Assert.That(announcer.IsNew("a")).IsFalse();
    await Assert.That(announcer.IsNew("b")).IsTrue();
    await Assert.That(announcer.IsNew("a")).IsTrue();
  }

  /// <summary>A Postgres provider whose only capability, pg_partman, reads one fixed state.</summary>
  private sealed class PgPartmanProvider(CapabilityState state) : ICapabilityProvider {
    public string Name => "postgres";
    public IReadOnlyList<CapabilityDescriptor> Descriptors { get; } =
      [PostgresExtensionCatalog.Describe(PostgresExtensionCatalog.BuiltIn.Single(e => e.Name == "pg_partman"))];
    public IReadOnlyList<string> Scopes { get; } = ["orders"];
    public Task<IReadOnlyList<CapabilityStatus>> ProbeAsync(string scope, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<CapabilityStatus>>([new CapabilityStatus(Descriptors[0], scope, state, "5.5.0", null, null)]);
  }

  private sealed class CapturingLogger<T> : ILogger<T> {
    private readonly List<(LogLevel Level, string? Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string? Message, Exception? Exception)> Entries {
      get { lock (_entries) { return [.. _entries]; } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_entries) { _entries.Add((logLevel, formatter(state, exception), exception)); }
    }
  }

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => now;
  }
}
