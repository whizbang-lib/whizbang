using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// That the runtime rewrite converts every stored form a table can hold, records the form it left
/// the table in, and stops scanning once a pass finds nothing to convert.
/// </summary>
/// <remarks>
/// <para>
/// Five shapes are in the field: renderings the old discovery never found, microsecond numbers,
/// day counts, tick counts, and rendered metadata timestamps. One pass converts all five, in one
/// transaction per table, and writes the ledger last in that transaction. A second pass converts
/// nothing and marks the table settled; a third pass does not scan it at all.
/// </para>
/// <para>
/// Run through the same phase the initializer runs, over the statements the runtime derives from
/// the model, against a scratch database carrying the ledger migration.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/CanonicalTemporalRewrite.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class CanonicalTemporalRewriteIntegrationTests : IAsyncDisposable {
  private const long LOCK_ID = 192837465;
  private const int TIMEOUT_SECONDS = 60;
  private const string TABLE_COLUMNS =
    "id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb NOT NULL DEFAULT '{}', scope jsonb NOT NULL DEFAULT '{}', "
    + "created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), version int NOT NULL DEFAULT 1";

  private static readonly DateTime _at = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    OpaqueDocumentFixture.EnsureRegistered();
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"rewrite_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    var migration = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public")
      .GetMigration("153_PerspectiveForms")!;
    await _executeAsync(migration.Sql);
    await _executeAsync($"CREATE TABLE wh_per_mapped ({TABLE_COLUMNS})");
    await _executeAsync($"CREATE TABLE wh_per_opaque ({TABLE_COLUMNS})");
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
      } catch (NpgsqlException) {
        // The container is torn down with the run; a database left behind costs nothing.
      }
    }
    GC.SuppressFinalize(this);
  }

  private async Task _executeAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }

  private async Task _seedAsync(string table, Guid id, string data, string metadata) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(
      $"INSERT INTO {table} (id, data, metadata) VALUES ($1, $2::jsonb, $3::jsonb)", db);
    command.Parameters.AddWithValue(id);
    command.Parameters.AddWithValue(data);
    command.Parameters.AddWithValue(metadata);
    await command.ExecuteNonQueryAsync();
  }

  private static RewriteContext _context() =>
    new(new DbContextOptionsBuilder<RewriteContext>()
      .UseNpgsql("Host=localhost;Database=probe;Username=u;Password=p", npgsql => npgsql.UseWhizbangFunctions())
      .Options);

  private ImmutableArray<(string Name, string Sql)> _rewrites() {
    using var context = _context();
    return CanonicalTemporalRewrite.ForModel(context.Model, PerspectiveDocumentSerialization.Options, "public");
  }

  private async Task<bool> _runAsync(IEnumerable<(string Name, string Sql)>? rewrites = null) =>
    await CanonicalTemporalRewritePhase.ApplyAsync(
      () => new NpgsqlConnection(_connectionString), LOCK_ID, rewrites ?? _rewrites(), TIMEOUT_SECONDS);

  private static string _micros(DateTime utc) =>
    CanonicalTemporalFormat.ToEpochMicroseconds(utc).ToString(CultureInfo.InvariantCulture);

  /// <summary>Every stored form a mapped table can hold is converted in one pass.</summary>
  [Test]
  public async Task OnePassConvertsEveryStoredFormAsync() {
    var renderings = Guid.CreateVersion7();
    var mixedUnits = Guid.CreateVersion7();
    await _seedAsync("wh_per_mapped", renderings,
      """{"OccurredAt":"2026-03-04T05:06:07Z","RecordedAt":"2026-03-04T05:06:07+00:00","Window":{"Opens":"09:30:00","Length":"01:30:00"},"Occurrences":[{"Day":"2026-03-05","MaybeAt":"2026-03-05T05:06:07Z"}]}""",
      """{"Timestamp":"2026-03-04T05:06:07Z"}""");
    await _seedAsync("wh_per_mapped", mixedUnits,
      $$"""{"OccurredAt":{{_micros(_at)}},"RecordedAt":{{_micros(_at)}},"Window":{"Opens":34200000000,"Length":54000000000},"Occurrences":[{"Day":20517,"MaybeAt":{{_micros(_at)}}}]}""",
      $$"""{"Timestamp":{{_micros(_at)}}}""");

    await Assert.That(await _runAsync()).IsTrue();

    var converted = JsonDocument.Parse(await _scalarAsync($"SELECT data::text FROM wh_per_mapped WHERE id = '{renderings}'")).RootElement;
    await Assert.That(converted.GetProperty("OccurredAt").GetInt64()).IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_at));
    await Assert.That(converted.GetProperty("RecordedAt").GetInt64()).IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_at))
      .Because("an inherited temporal is one the mapping reads, so the rewrite reaches it");
    await Assert.That(converted.GetProperty("Window").GetProperty("Opens").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToMicrosecondsOfDay(new TimeOnly(9, 30)));
    await Assert.That(converted.GetProperty("Window").GetProperty("Length").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToMicroseconds(TimeSpan.FromMinutes(90)));
    await Assert.That(converted.GetProperty("Occurrences")[0].GetProperty("Day").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(new DateOnly(2026, 3, 5)))
      .Because("a temporal inside a collection element is one the mapping reads");
    await Assert.That(converted.GetProperty("Occurrences")[0].GetProperty("MaybeAt").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(JsonDocument.Parse(await _scalarAsync($"SELECT metadata::text FROM wh_per_mapped WHERE id = '{renderings}'"))
        .RootElement.GetProperty("Timestamp").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_at))
      .Because("the framework's own document is a document like any other");

    var units = JsonDocument.Parse(await _scalarAsync($"SELECT data::text FROM wh_per_mapped WHERE id = '{mixedUnits}'")).RootElement;
    await Assert.That(units.GetProperty("Window").GetProperty("Length").GetInt64())
      .IsEqualTo(5400000000L)
      .Because("a tick count in the mixed-unit form divides down to microseconds");
    await Assert.That(units.GetProperty("Occurrences")[0].GetProperty("Day").GetInt64())
      .IsEqualTo(20517L * CanonicalTemporalFormat.MICROSECONDS_PER_DAY)
      .Because("a day count in the mixed-unit form multiplies up to microseconds at midnight");
    await Assert.That(units.GetProperty("OccurredAt").GetInt64()).IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_at))
      .Because("an instant was microseconds in every form and does not move");
    await Assert.That(units.GetProperty("Window").GetProperty("Opens").GetInt64()).IsEqualTo(34200000000L)
      .Because("a time of day was microseconds in every form and does not move");

    await Assert.That(await _scalarAsync("SELECT temporal_form::text || '/' || coalesce(settled_at::text, 'open') FROM wh_perspective_forms WHERE table_name = 'wh_per_mapped'"))
      .IsEqualTo("2/open")
      .Because("the pass that converted units records the microsecond form and is not yet known clean");
  }

  /// <summary>An opaque table's paths come from the serializer's metadata, and convert the same way.</summary>
  [Test]
  public async Task AnOpaqueTableConvertsThroughTheSerializersPathsAsync() {
    var id = Guid.CreateVersion7();
    await _seedAsync("wh_per_opaque", id,
      $$"""{"Id":"{{id}}","StartedAt":"2026-03-04T05:06:07Z","EndedAt":null,"Turns":[{"TurnId":"{{Guid.CreateVersion7()}}","Content":"a","At":"2026-03-04T05:07:07Z","Attachments":null}]}""",
      """{"Timestamp":"2026-03-04T05:06:07Z"}""");

    await _runAsync();

    var data = JsonDocument.Parse(await _scalarAsync($"SELECT data::text FROM wh_per_opaque WHERE id = '{id}'")).RootElement;
    await Assert.That(data.GetProperty("StartedAt").GetInt64()).IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_at));
    await Assert.That(data.GetProperty("Turns")[0].GetProperty("At").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_at.AddMinutes(1)))
      .Because("a positional record's parameter inside a collection is a placement the serializer reads");
    await Assert.That(data.GetProperty("EndedAt").ValueKind).IsEqualTo(JsonValueKind.Null)
      .Because("a null is left alone");
  }

  /// <summary>
  /// A second pass converts nothing and settles the table; a third pass does not scan it at all.
  /// </summary>
  [Test]
  public async Task ASecondPassSettlesAndAThirdSkipsAsync() {
    var id = Guid.CreateVersion7();
    await _seedAsync("wh_per_mapped", id, """{"OccurredAt":"2026-03-04T05:06:07Z"}""", "{}");

    await _runAsync();
    await Assert.That(await _scalarAsync("SELECT coalesce(settled_at::text, 'open') FROM wh_perspective_forms WHERE table_name = 'wh_per_mapped'"))
      .IsEqualTo("open");

    await _runAsync();
    await Assert.That(await _scalarAsync("SELECT coalesce(settled_at::text, 'open') FROM wh_perspective_forms WHERE table_name = 'wh_per_mapped'"))
      .IsNotEqualTo("open")
      .Because("a pass at the microsecond form that converts nothing is the evidence the table is clean");

    // A rendering written after settling proves the third pass does not scan: it stays a rendering.
    var late = Guid.CreateVersion7();
    await _seedAsync("wh_per_mapped", late, """{"OccurredAt":"2026-03-04T05:06:07Z"}""", "{}");
    await _runAsync();
    await Assert.That(await _scalarAsync($"SELECT jsonb_typeof(data -> 'OccurredAt') FROM wh_per_mapped WHERE id = '{late}'"))
      .IsEqualTo("string")
      .Because("a settled table is skipped without a scan; the reader accepts the rendering and counts it");
  }

  /// <summary>A table that does not exist yet is skipped, and nothing is recorded for it.</summary>
  [Test]
  public async Task AMissingTableIsSkippedAsync() {
    await _executeAsync("DROP TABLE wh_per_opaque");

    await Assert.That(await _runAsync()).IsTrue();

    await Assert.That(await _scalarAsync("SELECT count(*)::text FROM wh_perspective_forms WHERE table_name = 'wh_per_opaque'"))
      .IsEqualTo("0")
      .Because("at this point in startup the table frequently does not exist yet, which is ordinary");
  }

  /// <summary>
  /// A failure inside a table's pass leaves its ledger row untouched, and the other tables convert.
  /// </summary>
  /// <remarks>
  /// One transaction per table is the whole point: the ledger cannot say "converted" about a table
  /// whose conversion did not finish, and one table's trouble is not another's.
  /// </remarks>
  [Test]
  public async Task AFailedPassLeavesTheLedgerUntouchedAsync() {
    await _executeAsync("ALTER TABLE wh_per_opaque DROP COLUMN metadata");
    var id = Guid.CreateVersion7();
    await _seedAsync("wh_per_mapped", id, """{"OccurredAt":"2026-03-04T05:06:07Z"}""", "{}");

    await _runAsync();

    await Assert.That(await _scalarAsync("SELECT count(*)::text FROM wh_perspective_forms WHERE table_name = 'wh_per_opaque'"))
      .IsEqualTo("0")
      .Because("the statement that failed was one transaction, ledger write included");
    await Assert.That(await _scalarAsync("SELECT temporal_form::text FROM wh_perspective_forms WHERE table_name = 'wh_per_mapped'"))
      .IsEqualTo("2")
      .Because("one table's failure is reported, not spread");
    await Assert.That(await _scalarAsync($"SELECT jsonb_typeof(data -> 'OccurredAt') FROM wh_per_mapped WHERE id = '{id}'"))
      .IsEqualTo("number");
  }

  /// <summary>
  /// An index over a rewritten extraction builds after the pass, which is what the pass is for.
  /// </summary>
  [Test]
  public async Task AnIndexBuildsOverTheRewrittenExtractionAsync() {
    await _seedAsync("wh_per_mapped", Guid.CreateVersion7(), """{"OccurredAt":"2026-03-04T05:06:07Z"}""", "{}");

    await Assert.That(async () => await _executeAsync(
        "CREATE INDEX ix_probe_before ON wh_per_mapped (((data ->> 'OccurredAt')::bigint))"))
      .Throws<PostgresException>()
      .Because("a rendering cannot be cast to bigint, so the index refuses to build over it");

    await _runAsync();

    await Assert.That(async () => await _executeAsync(
        "CREATE INDEX ix_probe_after ON wh_per_mapped (((data ->> 'OccurredAt')::bigint))"))
      .ThrowsNothing();
  }
}
