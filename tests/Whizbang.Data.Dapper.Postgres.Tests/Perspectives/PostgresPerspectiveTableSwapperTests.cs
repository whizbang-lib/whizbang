using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// The PostgreSQL shadow table and swap behind a blue-green rebuild (#1025), against a real database: the shadow
/// table has the live one's shape, the swap is atomic and keeps the index names in place, readers are never blocked
/// while the swap holds writers back, a writer that was held continues against the new table, and a swap that cannot
/// get its locks leaves the live table as it was.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Perspectives/PostgresPerspectiveTableSwapper.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperPostgresPerspectiveStore.cs</code-under-test>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class PostgresPerspectiveTableSwapperTests : PostgresTestBase {
  private const string LIVE = "wh_per_swap_order";
  private const string SHADOW = LIVE + "_bg";
  private const string PREVIOUS = LIVE + "_bg_old";

  private PostgresPerspectiveTableSwapper _swapper() => new(async ct => {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(ct);
    return connection;
  }, schema: null);

  private async Task _createLiveAsync() {
    await using var conn = await _openAsync();
    await conn.ExecuteAsync($$"""
      CREATE TABLE {{LIVE}} (
        id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb, scope jsonb NOT NULL DEFAULT '{}',
        created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1, lane text NOT NULL DEFAULT 'cold');
      CREATE INDEX idx_swap_order_lane ON {{LIVE}} (lane);
      CREATE UNIQUE INDEX idx_swap_order_data ON {{LIVE}} ((data->>'Number'));
      """);
  }

  [Test]
  public async Task CreateShadow_HasTheLiveTablesShape_AndReplacesALeftoverAsync() {
    await _createLiveAsync();
    await using (var conn = await _openAsync()) {
      await conn.ExecuteAsync($"CREATE TABLE {SHADOW} (leftover int)");
    }

    var shadow = await _swapper().CreateShadowAsync(LIVE, CancellationToken.None);

    await Assert.That(shadow).IsEqualTo(SHADOW);
    await using var verify = await _openAsync();
    var columns = (await verify.QueryAsync<string>(
      "SELECT column_name FROM information_schema.columns WHERE table_name = @t ORDER BY ordinal_position", new { t = SHADOW })).ToList();
    await Assert.That(columns).IsEquivalentTo(["id", "data", "metadata", "scope", "created_at", "updated_at", "version", "lane"]);
    await Assert.That(await _indexCountAsync(verify, SHADOW)).IsEqualTo(3)
      .Because("The primary key the upsert conflicts on, and every index the live table has.");
  }

  [Test]
  public async Task Swap_KeepsThePreviousTable_AndTheIndexNamesStayWithTheLiveTableAsync() {
    await _createLiveAsync();
    var swapper = _swapper();
    var old = Guid.NewGuid();
    var rebuilt = Guid.NewGuid();
    await _insertAsync(LIVE, old, "1");
    await swapper.CreateShadowAsync(LIVE, CancellationToken.None);
    await _insertAsync(SHADOW, rebuilt, "2");

    var previous = await swapper.SwapAsync(new PerspectiveTableSwap(LIVE, SHADOW, KeepPrevious: true, TimeSpan.FromSeconds(10)),
      _ => Task.CompletedTask, CancellationToken.None);

    await Assert.That(previous).IsEqualTo(PREVIOUS);
    await using var verify = await _openAsync();
    await Assert.That((await verify.QueryAsync<Guid>($"SELECT id FROM {LIVE}")).ToList()).IsEquivalentTo([rebuilt]);
    await Assert.That((await verify.QueryAsync<Guid>($"SELECT id FROM {PREVIOUS}")).ToList()).IsEquivalentTo([old]);
    await Assert.That(await _indexNamesAsync(verify, LIVE)).IsEquivalentTo([LIVE + "_pkey", "idx_swap_order_data", "idx_swap_order_lane"])
      .Because("The schema pass creates indexes by name; finding them under the live table it creates no duplicates.");
    await Assert.That(await _indexNamesAsync(verify, PREVIOUS))
      .IsEquivalentTo([LIVE + "_pkey_bg_old", "idx_swap_order_data_bg_old", "idx_swap_order_lane_bg_old"]);
    await Assert.That(await verify.QuerySingleAsync<bool>($"SELECT to_regclass('{SHADOW}') IS NULL")).IsTrue();
  }

  [Test]
  public async Task Swap_DroppingThePrevious_AndASecondSwapReplacesAKeptOneAsync() {
    await _createLiveAsync();
    var swapper = _swapper();
    await swapper.CreateShadowAsync(LIVE, CancellationToken.None);
    await swapper.SwapAsync(new PerspectiveTableSwap(LIVE, SHADOW, true, TimeSpan.FromSeconds(10)), _ => Task.CompletedTask, CancellationToken.None);
    await swapper.CreateShadowAsync(LIVE, CancellationToken.None);
    await swapper.SwapAsync(new PerspectiveTableSwap(LIVE, SHADOW, true, TimeSpan.FromSeconds(10)), _ => Task.CompletedTask, CancellationToken.None);
    await swapper.CreateShadowAsync(LIVE, CancellationToken.None);

    var previous = await swapper.SwapAsync(new PerspectiveTableSwap(LIVE, SHADOW, false, TimeSpan.FromSeconds(10)), _ => Task.CompletedTask, CancellationToken.None);

    await Assert.That(previous).IsNull();
    await using var verify = await _openAsync();
    await Assert.That(await _indexNamesAsync(verify, LIVE)).IsEquivalentTo([LIVE + "_pkey", "idx_swap_order_data", "idx_swap_order_lane"]);
    await Assert.That(await verify.QuerySingleAsync<int>(
      "SELECT count(*)::int FROM pg_class WHERE relkind = 'r' AND relname LIKE 'wh_per_swap_order%'")).IsEqualTo(2)
      .Because("The live table and the one kept from the swap before; a kept table is replaced, never accumulated.");
  }

  /// <summary>
  /// While the swap holds writers back for the last catch-up, a reader reads the live table at once, and a writer
  /// that was waiting writes the NEW table once the swap commits: PostgreSQL resolves the name again after the wait.
  /// </summary>
  [Test]
  public async Task Swap_ReadersAreNotBlocked_AndAHeldWriterContinuesAgainstTheNewTableAsync() {
    await _createLiveAsync();
    var swapper = _swapper();
    await _insertAsync(LIVE, Guid.NewGuid(), "1");
    await swapper.CreateShadowAsync(LIVE, CancellationToken.None);
    var written = Guid.NewGuid();
    Task? writer = null;
    var readDuringLock = -1;

    await swapper.SwapAsync(new PerspectiveTableSwap(LIVE, SHADOW, true, TimeSpan.FromSeconds(10)), async ct => {
      await using (var reader = await _openAsync()) {
        readDuringLock = await reader.QuerySingleAsync<int>($"SELECT count(*)::int FROM {LIVE}");
      }
      writer = _insertAsync(LIVE, written, "9");
      await _waitUntilALockWaitsOnAsync(LIVE, ct);
    }, CancellationToken.None);
    await writer!;

    await Assert.That(readDuringLock).IsEqualTo(1);
    await using var verify = await _openAsync();
    await Assert.That((await verify.QueryAsync<Guid>($"SELECT id FROM {LIVE}")).ToList()).IsEquivalentTo([written]);
    await Assert.That(await verify.QuerySingleAsync<int>($"SELECT count(*)::int FROM {PREVIOUS} WHERE id = @written", new { written })).IsEqualTo(0);
  }

  /// <summary>
  /// An index the schema pass created on the live table while the rebuild ran is not on the shadow. Keeping the
  /// previous table takes its name along, so the next schema pass creates it on the new live table by that name.
  /// </summary>
  [Test]
  public async Task Swap_AnIndexAddedDuringTheRebuild_LeavesItsNameFreeForTheSchemaPassAsync() {
    await _createLiveAsync();
    var swapper = new PostgresPerspectiveTableSwapper(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, schema: "public");
    await swapper.CreateShadowAsync(LIVE, CancellationToken.None);
    await using (var conn = await _openAsync()) {
      await conn.ExecuteAsync($"CREATE INDEX idx_swap_order_created ON {LIVE} (created_at)");
    }

    await swapper.SwapAsync(new PerspectiveTableSwap(LIVE, SHADOW, true, TimeSpan.FromSeconds(10)), _ => Task.CompletedTask, CancellationToken.None);

    await using var verify = await _openAsync();
    await Assert.That(await _indexNamesAsync(verify, PREVIOUS)).Contains("idx_swap_order_created_bg_old");
    await Assert.That(await _indexNamesAsync(verify, LIVE)).DoesNotContain("idx_swap_order_created");
    await verify.ExecuteAsync($"CREATE INDEX IF NOT EXISTS idx_swap_order_created ON {LIVE} (created_at)");
    await Assert.That(await _indexNamesAsync(verify, LIVE)).Contains("idx_swap_order_created")
      .Because("The name is free, so the schema pass's CREATE INDEX IF NOT EXISTS creates it on the new table.");
  }

  [Test]
  public async Task AddWhizbangPostgres_RegistersASwapperOnItsConnectionStringAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPostgres(ConnectionString, _json);
    await using var provider = services.BuildServiceProvider();

    var swapper = provider.GetRequiredService<IPerspectiveTableSwapper>();

    await Assert.That(await swapper.FindTableAsync("Tests.NotRegistered", CancellationToken.None)).IsNull();
  }

  [Test]
  public async Task Swap_ThatCannotGetItsLocks_RollsBack_AndLeavesTheLiveTableAsync() {
    await _createLiveAsync();
    var swapper = _swapper();
    var old = Guid.NewGuid();
    await _insertAsync(LIVE, old, "1");
    await swapper.CreateShadowAsync(LIVE, CancellationToken.None);
    await using var longReader = await _openAsync();
    await using var tx = await longReader.BeginTransactionAsync();
    await longReader.ExecuteAsync($"SELECT count(*) FROM {LIVE}", transaction: tx);

    await Assert.That(() => swapper.SwapAsync(new PerspectiveTableSwap(LIVE, SHADOW, true, TimeSpan.FromMilliseconds(200)), _ => Task.CompletedTask, CancellationToken.None))
      .Throws<PostgresException>();

    await tx.RollbackAsync();
    await using var verify = await _openAsync();
    await Assert.That((await verify.QueryAsync<Guid>($"SELECT id FROM {LIVE}")).ToList()).IsEquivalentTo([old]);
    await Assert.That(await verify.QuerySingleAsync<bool>($"SELECT to_regclass('{SHADOW}') IS NOT NULL")).IsTrue();
  }

  [Test]
  public async Task FindDeleteAndDrop_WorkOnTheNamedTablesAsync() {
    await _createLiveAsync();
    var swapper = _swapper();
    await using (var conn = await _openAsync()) {
      await conn.ExecuteAsync("""
        INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
        VALUES ('Tests.SwapPerspective', @t, '{}'::jsonb, 'h', 'svc')
        """, new { t = LIVE });
    }
    var keep = Guid.NewGuid();
    var gone = Guid.NewGuid();
    await _insertAsync(LIVE, keep, "1");
    await _insertAsync(LIVE, gone, "2");

    await Assert.That(await swapper.FindTableAsync("Tests.SwapPerspective", CancellationToken.None)).IsEqualTo(LIVE);
    await Assert.That(await swapper.FindTableAsync("Tests.Unknown", CancellationToken.None)).IsNull();
    await swapper.DeleteRowsAsync(LIVE, [gone], CancellationToken.None);
    await swapper.CreateShadowAsync(LIVE, CancellationToken.None);
    await swapper.DropAsync(SHADOW, CancellationToken.None);
    await swapper.DropAsync(SHADOW, CancellationToken.None);

    await using var verify = await _openAsync();
    await Assert.That((await verify.QueryAsync<Guid>($"SELECT id FROM {LIVE}")).ToList()).IsEquivalentTo([keep]);
    await Assert.That(await verify.QuerySingleAsync<bool>($"SELECT to_regclass('{SHADOW}') IS NULL")).IsTrue();
  }

  /// <summary>
  /// A blue-green rebuild's flow redirects the Dapper store to the shadow table for every read and write, and no
  /// other flow is affected.
  /// </summary>
  [Test]
  public async Task Store_InARedirectedFlow_ReadsAndWritesTheShadowTableOnlyAsync() {
    await _createLiveAsync();
    await _swapper().CreateShadowAsync(LIVE, CancellationToken.None);
    var store = new DapperPostgresPerspectiveStore<SwapModel>(ConnectionString, LIVE, _json);
    var id = Guid.NewGuid();

    using (PerspectiveTableRedirect.Begin(LIVE, SHADOW)) {
      await store.UpsertAsync(id, new SwapModel { Number = "7" }, new PerspectiveScope { TenantId = "t" });
      await Assert.That((await store.GetByStreamIdAsync(id))!.Number).IsEqualTo("7");
      await Assert.That((await store.ReadForApplyAsync(id)).Version.State).IsEqualTo(PerspectiveRowVersionState.Present);
    }

    await Assert.That(await store.GetByStreamIdAsync(id)).IsNull()
      .Because("The live table never saw the redirected write.");
    using (PerspectiveTableRedirect.Begin(LIVE, SHADOW)) {
      await store.PurgeAsync(id);
      await Assert.That(await store.GetByStreamIdAsync(id)).IsNull();
    }
  }

  [Test]
  public async Task Names_StayInsideTheIdentifierLimit_AndDistinctAsync() {
    var a = "wh_per_" + new string('a', 52) + "_one";
    var b = "wh_per_" + new string('a', 52) + "_two";

    await Assert.That(PostgresPerspectiveTableSwapper.ShadowName("wh_per_x")).IsEqualTo("wh_per_x_bg");
    await Assert.That(PostgresPerspectiveTableSwapper.PreviousName(a).Length).IsLessThanOrEqualTo(63);
    await Assert.That(PostgresPerspectiveTableSwapper.PreviousName(a)).IsNotEqualTo(PostgresPerspectiveTableSwapper.PreviousName(b))
      .Because("PostgreSQL truncates a long name silently; the hash keeps two long names that share a start apart.");
    await Assert.That(() => PostgresPerspectiveTableSwapper.ShadowName(" ")).ThrowsExactly<ArgumentException>();
    await Assert.That(() => new PostgresPerspectiveTableSwapper(null!, null)).ThrowsExactly<ArgumentNullException>();
  }

  internal sealed class SwapModel {
    public string Number { get; set; } = "";
  }

  private static readonly JsonSerializerOptions _json = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private async Task _insertAsync(string table, Guid id, string number) {
    await using var conn = await _openAsync();
    await conn.ExecuteAsync($"INSERT INTO {table} (id, data) VALUES (@id, jsonb_build_object('Number', @number))", new { id, number });
  }

  private static async Task<int> _indexCountAsync(NpgsqlConnection conn, string table) =>
    await conn.QuerySingleAsync<int>("SELECT count(*)::int FROM pg_indexes WHERE tablename = @table", new { table });

  private static async Task<List<string>> _indexNamesAsync(NpgsqlConnection conn, string table) =>
    [.. await conn.QueryAsync<string>("SELECT indexname FROM pg_indexes WHERE tablename = @table", new { table })];

  // The condition the held writer is in once it waits on the swap's lock: a lock on the table that is not granted.
  private async Task _waitUntilALockWaitsOnAsync(string table, CancellationToken cancellationToken) {
    using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    bounded.CancelAfter(TimeSpan.FromSeconds(30));
    await using var probe = await _openAsync();
    while (!await probe.QuerySingleAsync<bool>(
        "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE relation = to_regclass(@table) AND NOT granted)", new { table })) {
      await Task.Delay(20, bounded.Token);
    }
  }
}
