// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Processes;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2300: a perspective table whose fillfactor is lowered while it holds rows is rewritten once by an online strategy,
/// whatever its bloat, so the rows already stored get the free page space the new fillfactor reserves. With no online
/// strategy the request waits, the start says so, and <c>VACUUM FULL</c> never takes it.
/// </summary>
/// <remarks>
/// The test image has neither pg_squeeze nor the pg_repack client, so the online strategy here is pg_repack in process
/// with its client stood in, as <see cref="ExternalTableRewriteTests"/> does: the stand-in connects only with the
/// environment the coordinator hands the client and rewrites the table into a new heap, which is what pg_repack's copy
/// does, filling each page to the table's fillfactor. Everything that decides is real: the schema pass that lowers the
/// fillfactor and records why, the candidate query, the maintenance step, its capability check and budget, the
/// coordinator's hand-off and re-measure, and the clear.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/PerspectiveStorageParameters.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/TableMaintenanceSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/OnlineTableRewriteStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/TableRewriteStartupStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/405_AFillFactorDecreaseIsRewrittenOnce.sql</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class FillFactorDecreaseRewriteTests : EFCoreTestBase {
  private const string TABLE = "wh_per_ff_decrease";

  /// <summary>A compact table holding rows, packed full at the default fillfactor (100), and analyzed.</summary>
  private async Task<Guid> _compactTableAsync(int? fillFactor = null) {
    var with = fillFactor is { } value ? $" WITH (fillfactor = {value})" : "";
    await _execAsync($"""
      CREATE TABLE {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL, version bigint NOT NULL DEFAULT 1){with};
      INSERT INTO {TABLE} (id, data)
      SELECT gen_random_uuid(), jsonb_build_object('n', g, 'pad', repeat('x', 80)) FROM generate_series(1, 3000) g;
      ANALYZE {TABLE};
      """);
    return await _scalarAsync<Guid>($"SELECT id FROM {TABLE} WHERE ctid = '(0,1)'");
  }

  private async Task _reconcileAsync(int fillFactor) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await PerspectiveStorageParameters.ReconcileAsync(
      db, "public", [PerspectiveStorageParameters.MARKER + TABLE + " fillfactor=" + fillFactor], null, CancellationToken.None);
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(IServiceProvider services) {
    var options = new DbContextOptionsBuilder<WorkCoordinationDbContext>()
      .UseApplicationServiceProvider(services)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .UseNpgsql(ConnectionString)
      .Options;
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(new WorkCoordinationDbContext(options), JsonContextRegistry.CreateCombinedOptions());
  }

  private async Task<TableRewriteCandidate?> _candidateAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var candidates = await _coordinator(provider).GetTablesNeedingRewriteAsync(CancellationToken.None);
    return candidates.Where(c => c.TableName == TABLE).Cast<TableRewriteCandidate?>().SingleOrDefault();
  }

  /// <summary>One maintenance cycle's online rewrite step, under the given strategy, with the pg_repack client stood in.</summary>
  private async Task _onlineCycleAsync(TableRewriteStrategy strategy, StandInClient client) {
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var settings = new MaintenanceWorkerOptions { TableRewriteStrategy = strategy };
    settings.TableRewrite.PgRepack.Mode = PgRepackMode.InProcess;
    var options = Options.Create(settings);
    var capabilities = await ReadyCapabilities.CreateAsync();
    var step = new OnlineTableRewriteStep(
      [new PgSqueezeTableRewriter(), new PgRepackTableRewriter(options, client, capabilities, NullLogger<PgRepackTableRewriter>.Instance)],
      options, capabilities, NullLogger<OnlineTableRewriteStep>.Instance);
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(_coordinator(provider)).BuildServiceProvider();
    await step.RunAsync(services, CancellationToken.None);
  }

  /// <summary>The boot-time Rewrite step, with the real VACUUM FULL rewriter, under the given strategy and permission.</summary>
  private async Task<(StartupStepReport Report, CapturingLogger<TableRewriteStartupStep> Logger)> _bootAsync(
      TableRewriteStrategy strategy, bool allowTableRewrite) {
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(_coordinator(provider)).BuildServiceProvider();
    var options = Options.Create(new MaintenanceWorkerOptions { TableRewriteStrategy = strategy, AllowTableRewrite = allowTableRewrite });
    var logger = new CapturingLogger<TableRewriteStartupStep>();
    var step = new TableRewriteStartupStep(services.GetRequiredService<IServiceScopeFactory>(),
      [new VacuumFullTableRewriter(options)], options, capabilities: null, logger);
    return (await step.ExecuteAsync(CancellationToken.None), logger);
  }

  [Test]
  [Timeout(120000)]
  public async Task AFillFactorDecrease_OnACompactTable_IsRewrittenOnceOnline_AndAnUpdateOfAnOldRowIsHotAsync(CancellationToken cancellationToken) {
    var oldRow = await _compactTableAsync();
    await Assert.That(await _isHotUpdateAsync(oldRow)).IsFalse()
      .Because("the control: packed at fillfactor 100, the row's page has no room for its new version");

    await _reconcileAsync(90);

    var candidate = await _candidateAsync();
    await Assert.That(candidate.HasValue).IsTrue().Because("a lowered fillfactor is offered whatever the table's bloat");
    await Assert.That(candidate!.Value.Reason).IsEqualTo(TableRewriteReason.FillFactorDecrease);
    await Assert.That(candidate.Value.Requested).IsTrue();
    await Assert.That(candidate.Value.BloatRatio).IsLessThan(candidate.Value.RewriteThreshold)
      .Because("the table is compact: under the bloat rule alone it would never be rewritten");

    var client = new StandInClient(ConnectionString);
    await _onlineCycleAsync(TableRewriteStrategy.PgRepack, client);

    await Assert.That(client.Tables).Count().IsEqualTo(1);
    await Assert.That(await _isHotUpdateAsync(oldRow)).IsTrue()
      .Because("rewritten at fillfactor 90, every page keeps a tenth free, so the old row's update stays on its page");
    await Assert.That(await _pendingAsync()).DoesNotContain(TABLE);
    await Assert.That(await _candidateAsync()).IsNull();

    await _onlineCycleAsync(TableRewriteStrategy.PgRepack, client);

    await Assert.That(client.Tables).Count().IsEqualTo(1)
      .Because("the request was honored; the next cycle does not rewrite the table again for the same change");
  }

  [Test]
  [Timeout(120000)]
  public async Task WithNoOnlineStrategy_TheRequestWaits_VacuumFullNeverRuns_AndTheStartSaysSoAsync(CancellationToken cancellationToken) {
    await _compactTableAsync();
    await _reconcileAsync(90);
    var before = await _relfilenodeAsync();
    var client = new StandInClient(ConnectionString);

    await _onlineCycleAsync(TableRewriteStrategy.Auto, client);
    var (report, logger) = await _bootAsync(TableRewriteStrategy.Auto, allowTableRewrite: true);

    await Assert.That(client.Tables).IsEmpty();
    await Assert.That(await _relfilenodeAsync()).IsEqualTo(before)
      .Because("VACUUM FULL is permitted for bloat and still never takes a lowered fillfactor: the heap was not rewritten");
    await Assert.That(report.Reason).IsEqualTo("no rewrites owed");
    await Assert.That(await _pendingAsync()).Contains(TABLE).Because("the request stays pending for an online strategy");
    var waiting = logger.Messages.Where(m => m.Contains("lowered fillfactor", StringComparison.Ordinal)).ToList();
    await Assert.That(waiting).Count().IsEqualTo(1);
    await Assert.That(waiting[0]).Contains(TABLE).And.Contains("PgSqueeze").And.Contains("PgRepack");
  }

  [Test]
  [Timeout(120000)]
  public async Task WithTheRewritePolicyOff_NothingIsRewrittenAsync(CancellationToken cancellationToken) {
    await _compactTableAsync();
    await _reconcileAsync(90);
    var before = await _relfilenodeAsync();
    var client = new StandInClient(ConnectionString);

    await _onlineCycleAsync(TableRewriteStrategy.None, client);
    var (report, _) = await _bootAsync(TableRewriteStrategy.None, allowTableRewrite: true);

    await Assert.That(client.Tables).IsEmpty();
    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(await _relfilenodeAsync()).IsEqualTo(before);
    await Assert.That(await _pendingAsync()).Contains(TABLE);
  }

  [Test]
  [Timeout(120000)]
  public async Task AFillFactorIncrease_IsNotOffered_ForTheBloatRuleAloneDecidesAsync(CancellationToken cancellationToken) {
    await _compactTableAsync(fillFactor: 70);

    await _reconcileAsync(90);

    await Assert.That(await _pendingAsync()).Contains(TABLE).Because("the request is recorded as it always was");
    await Assert.That(await _candidateAsync()).IsNull()
      .Because("a raised fillfactor gives the pages no less room than they have; the table waits for bloat as before");
  }

  /// <summary>
  /// What the reason row records: the fillfactor the pages were packed at before the first pending change, and the one
  /// in force now. A later change keeps the first; one that brings the fillfactor back to where the pages were packed
  /// leaves no decrease to honor, and the clear that follows a rewrite removes it.
  /// </summary>
  [Test]
  public async Task TheRequest_RecordsTheDecreaseFromWhereThePagesWerePackedAsync() {
    await _execAsync("SELECT wh_request_fillfactor_rewrite('wh_per_a', 100, 90)");
    await Assert.That(await _decreaseAsync("wh_per_a")).IsEqualTo("100->90");
    await _execAsync("SELECT wh_request_fillfactor_rewrite('wh_per_a', 90, 95)");
    await Assert.That(await _decreaseAsync("wh_per_a")).IsEqualTo("100->95")
      .Because("the pages are still packed at 100, which is fuller than 95");
    await _execAsync("SELECT wh_request_fillfactor_rewrite('wh_per_a', 95, 100)");
    await Assert.That(await _decreaseAsync("wh_per_a")).IsNull().Because("back at 100, nothing is packed fuller than the fillfactor");

    await _execAsync("SELECT wh_request_fillfactor_rewrite('wh_per_b', 80, 90)");
    await Assert.That(await _decreaseAsync("wh_per_b")).IsNull().Because("an increase is not a decrease");

    await _execAsync("SELECT wh_request_fillfactor_rewrite('wh_per_c', 100, 70); SELECT wh_clear_table_rewrite('wh_per_c');");
    await Assert.That(await _decreaseAsync("wh_per_c")).IsNull().Because("a cleared request takes its reason with it");
    var pending = (await _pendingAsync()).Split(',');
    await Assert.That(pending.Count(t => t == "wh_per_a")).IsEqualTo(1)
      .Because("each call records the request itself, as wh_request_table_rewrite does, once per table");
    await Assert.That(pending).Contains("wh_per_b").And.DoesNotContain("wh_per_c");
  }

  // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Whether an update of the row (to a column no index covers) is a HOT update, read inside the updating transaction
  /// from its own counter, so no statistics flush is waited on. The update is rolled back.
  /// </summary>
  private async Task<bool> _isHotUpdateAsync(Guid id) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var tx = await db.BeginTransactionAsync();
    await using (var update = new NpgsqlCommand($"UPDATE {TABLE} SET version = version + 1 WHERE id = @id", db, tx)) {
      update.Parameters.AddWithValue(nameof(id), id);
      await update.ExecuteNonQueryAsync();
    }
    await using var hot = new NpgsqlCommand($"SELECT pg_stat_get_xact_tuples_hot_updated('{TABLE}'::regclass)", db, tx);
    var count = (long)(await hot.ExecuteScalarAsync())!;
    await tx.RollbackAsync();
    return count == 1;
  }

  private Task<long> _relfilenodeAsync() => _scalarAsync<long>($"SELECT relfilenode::bigint FROM pg_class WHERE oid = '{TABLE}'::regclass");

  private Task<string> _pendingAsync() =>
    _scalarAsync<string>("SELECT setting_value FROM wh_settings WHERE setting_key = 'pending_table_rewrites'");

  private async Task<string?> _decreaseAsync(string table) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT from_fillfactor || '->' || to_fillfactor FROM wh_fillfactor_rewrite_requests WHERE table_name = @t", db);
    command.Parameters.AddWithValue("t", table);
    return await command.ExecuteScalarAsync() as string;
  }

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<T> _scalarAsync<T>(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (T)(await command.ExecuteScalarAsync())!;
  }

  private sealed class CapturingLogger<T> : ILogger<T> {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Messages.Add(formatter(state, exception));
  }

  /// <summary>
  /// Stands in for the pg_repack client: connects with exactly the environment it was handed and rewrites the table
  /// it was given into a new heap (what pg_repack's copy and swap produce), then reports a clean exit.
  /// </summary>
  private sealed class StandInClient(string connectionString) : IExternalProcessRunner {
    public List<string> Tables { get; } = [];

    public async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken) {
      var table = request.Arguments[^1]["--table=".Length..];
      Tables.Add(table);
      var env = request.Environment;
      var connection = new NpgsqlConnectionStringBuilder(connectionString) {
        Host = env["PGHOST"],
        Port = int.Parse(env["PGPORT"], System.Globalization.CultureInfo.InvariantCulture),
        Database = env["PGDATABASE"],
        Username = env["PGUSER"],
      }.ConnectionString;
      await using var db = new NpgsqlConnection(connection);
      await db.OpenAsync(cancellationToken);
      await using var command = new NpgsqlCommand($"VACUUM (FULL) {table}", db);
      await command.ExecuteNonQueryAsync(cancellationToken);
      return new ExternalProcessResult(true, 0, "", "");
    }
  }

  /// <summary>A capability registry in which the pg_repack extension and its client read Ready.</summary>
  private sealed class ReadyCapabilities(string name, string probedScope, CapabilityDescriptor descriptor) : ICapabilityProvider {
    public string Name { get; } = name;
    public IReadOnlyList<CapabilityDescriptor> Descriptors { get; } = [descriptor];
    public IReadOnlyList<string> Scopes { get; } = [probedScope];

    public Task<IReadOnlyList<CapabilityStatus>> ProbeAsync(string scope, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<CapabilityStatus>>([new CapabilityStatus(descriptor, scope, CapabilityState.Ready, "1.0", null, null)]);

    public static async Task<CapabilityRegistry> CreateAsync() {
      var extension = new CapabilityDescriptor(new CapabilityId(RequiresPostgresExtensionAttribute.PROVIDER, "pg_repack"),
        "online rewrite", ["Online table rewrite"], true, [], new Dictionary<CapabilityState, string>());
      var registry = new CapabilityRegistry(
        [new ReadyCapabilities(RequiresPostgresExtensionAttribute.PROVIDER, "db", extension),
         new ReadyCapabilities(PgRepackClient.PROVIDER, "this-host", PgRepackClient.Descriptor)],
        NullLogger<CapabilityRegistry>.Instance);
      await registry.RefreshAsync(CancellationToken.None);
      return registry;
    }
  }
}
