using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The forms migration 182 derives from a column's type carry every value between the column and the document
/// exactly, both ways, in the form the document stores it: dates and times as microsecond counts, a uuid or a
/// string-like value as a JSON string, an array as a JSON array, a jsonb value as it is.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#storage-moves</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PhysicalColumnFormsTests {
  private const string TABLE = "wh_forms_probe";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("phys_forms");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await PhysicalMoves.StartAsync(_connectionString);
    await PhysicalMoves.ExecAsync(_connectionString, $$"""
      CREATE DOMAIN wh_probe_code AS text;
      CREATE TABLE {{TABLE}} (
        id integer PRIMARY KEY, data jsonb NOT NULL DEFAULT '{}',
        t text, v varchar(8), u uuid, i2 smallint, i4 integer, i8 bigint, n numeric, f4 real, f8 double precision,
        b boolean, ts timestamptz, tn timestamp, d date, tm time, j jsonb, js json, ua uuid[], ta text[], ia integer[],
        dc wh_probe_code, p point, iv interval, tsa timestamptz[], m money);
      INSERT INTO {{TABLE}} (id, t, v, u, i2, i4, i8, n, f4, f8, b, ts, tn, d, tm, j, js, ua, ta, ia, dc)
      VALUES (1, 'text', 'short', '8a1f3c4e-0000-4000-8000-000000000001', -3, 2147483647, 9007199254740993, 12.3400,
              1.25, 0.1, true, TIMESTAMPTZ '2026-03-04 05:06:07.123456+00', TIMESTAMP '1969-12-31 23:59:59.999999',
              DATE '2026-03-04', TIME '23:59:59.999999', '{"a": [1, {"b": null}]}', '{"c": 1}',
              ARRAY['8a1f3c4e-0000-4000-8000-000000000001'::uuid], ARRAY['x', 'y z'], ARRAY[3, 1, 2], 'code-1');
      """);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private async Task<(string? Extraction, string? DocumentForm)> _formsAsync(string column) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(
      $"SELECT extraction, document_form FROM wh_physical_column_forms('{TABLE}'::regclass, '{column}', 'K')", db);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    var extraction = await reader.IsDBNullAsync(0) ? null : reader.GetString(0);
    var documentForm = await reader.IsDBNullAsync(1) ? null : reader.GetString(1);
    return (extraction, documentForm);
  }

  private Task<string> _scalarAsync(string sql) => PhysicalMoves.ScalarAsync(_connectionString, sql);

  /// <summary>
  /// Each supported column, written into the document with its document form and read back with its extraction,
  /// is the value it started as; and the document holds the form the framework's writers store.
  /// </summary>
  [Test]
  [Arguments("t", "\"text\"")]
  [Arguments("v", "\"short\"")]
  [Arguments("u", "\"8a1f3c4e-0000-4000-8000-000000000001\"")]
  [Arguments("i2", "-3")]
  [Arguments("i4", "2147483647")]
  [Arguments("i8", "9007199254740993")]
  [Arguments("n", "12.3400")]
  [Arguments("f4", "1.25")]
  [Arguments("f8", "0.1")]
  [Arguments("b", "true")]
  [Arguments("ts", "1772600767123456")]
  [Arguments("tn", "-1")]
  [Arguments("d", "1772582400000000")]
  [Arguments("tm", "86399999999")]
  [Arguments("j", "{\"a\": [1, {\"b\": null}]}")]
  [Arguments("js", "{\"c\": 1}")]
  [Arguments("ua", "[\"8a1f3c4e-0000-4000-8000-000000000001\"]")]
  [Arguments("ta", "[\"x\", \"y z\"]")]
  [Arguments("ia", "[3, 1, 2]")]
  [Arguments("dc", "\"code-1\"")]
  public async Task EachSupportedType_RoundTripsThroughTheDocumentAsync(string column, string document) {
    var (extraction, documentForm) = await _formsAsync(column);

    await Assert.That(extraction).IsNotNull();
    await Assert.That(documentForm).IsNotNull();
    await PhysicalMoves.ExecAsync(_connectionString,
      $"UPDATE {TABLE} SET data = jsonb_set(data, '{{K}}', {documentForm}, true) WHERE id = 1");
    await Assert.That(await _scalarAsync($"SELECT data -> 'K' FROM {TABLE} WHERE id = 1")).IsEqualTo(document);
    await Assert.That(await _scalarAsync($"SELECT ({extraction})::text IS NOT DISTINCT FROM {column}::text FROM {TABLE} WHERE id = 1"))
      .IsEqualTo("True");
  }

  /// <summary>A JSON null and a missing key both read back as a null column.</summary>
  [Test]
  [Arguments("j")]
  [Arguments("i4")]
  [Arguments("ua")]
  public async Task ANullOrMissingDocumentValue_IsANullColumnAsync(string column) {
    var (extraction, _) = await _formsAsync(column);

    await Assert.That(await _scalarAsync(
      $"SELECT concat_ws('|', ({extraction}) IS NULL, (SELECT ({extraction}) IS NULL FROM (SELECT '{{\"K\": null}}'::jsonb AS data) n)) FROM {TABLE} WHERE id = 1"))
      .IsEqualTo("true|true");
  }

  /// <summary>A type the document cannot hold exactly has no forms; neither has a column that is not there.</summary>
  [Test]
  [Arguments("p")]
  [Arguments("iv")]
  [Arguments("tsa")]
  [Arguments("m")]
  [Arguments("missing")]
  public async Task ATypeTheDocumentCannotHold_HasNoFormsAsync(string column) {
    var (extraction, documentForm) = await _formsAsync(column);

    await Assert.That(extraction).IsNull();
    await Assert.That(documentForm).IsNull();
  }
}
