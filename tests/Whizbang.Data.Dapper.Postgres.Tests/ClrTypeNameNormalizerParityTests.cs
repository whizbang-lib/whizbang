using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The SQL type-name normalizers are contractual surface (#1034): <c>wh_normalize_clr_type_name</c> agrees byte for
/// byte with <see cref="EventTypeMatchingHelper.NormalizeTypeName"/>, and with <see cref="TypeNameFormatter.Format"/>
/// for every type whose name carries no type arguments. The old names stay as aliases. The consumer-data helpers
/// (#1035) find assembly-qualified names in any table and normalize the ones inside perspective snapshots.
/// </summary>
/// <docs>operations/infrastructure/migrations#normalizing-type-names</docs>
[Category("Integration")]
public class ClrTypeNameNormalizerParityTests : IAsyncDisposable {
  private const string ASM = "Some.Assembly";
  private const string DECORATION = ", Version=1.54.0.0, Culture=neutral, PublicKeyToken=null";
  private const string OTHER_DECORATION = ", Version=0.0.0.0, Culture=neutral, PublicKeyToken=0123456789abcdef";

  private string? _testDatabaseName;
  private string? _connectionString;

  /// <summary>A type nested in another, whose CLR name carries a '+'.</summary>
  public sealed class Nested;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _testDatabaseName = $"test_{Guid.NewGuid():N}";
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true,
    }.ConnectionString;
    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_testDatabaseName is null) {
      return;
    }
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName} WITH (FORCE)");
    _testDatabaseName = null;
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// Every spelling a stored name has been seen in, nested and generic forms included: the two shapes where a
  /// truncation at the first <c>, Version=</c> and a careful strip disagree.
  /// </summary>
  [Test]
  [Arguments("Ns.Plain, " + ASM)]
  [Arguments("Ns.Plain, " + ASM + DECORATION)]
  [Arguments("Ns.Plain, " + ASM + OTHER_DECORATION)]
  [Arguments("Ns.Outer+Inner, " + ASM + DECORATION)]
  [Arguments("Ns.Box`1[[Ns.Inner, Inner.Asm" + DECORATION + "]], " + ASM + DECORATION)]
  [Arguments("Ns.Box`2[[Ns.A, A.Asm" + DECORATION + "],[Ns.Outer+B, B.Asm" + OTHER_DECORATION + "]], " + ASM + DECORATION)]
  [Arguments("Ns.Box`1[[Ns.Inner, Inner.Asm" + DECORATION + "]], " + ASM)]
  [Arguments("Ns.Plain, " + ASM + ",Version=1.0.0.0,Culture=neutral,PublicKeyToken=null")]
  [Arguments("Ns.Plain, " + ASM + ", Version=1.0.0.0")]
  [Arguments("Ns.Plain, " + ASM + ", Culture=neutral")]
  [Arguments("Ns.Plain")]
  [Arguments("")]
  public async Task SqlNormalizer_AgreesWithTheCSharpHelperAsync(string stored) {
    var expected = EventTypeMatchingHelper.NormalizeTypeName(stored);

    await Assert.That(await _scalarAsync("SELECT wh_normalize_clr_type_name(@Name)", stored)).IsEqualTo(expected);
    await Assert.That(await _scalarAsync("SELECT normalize_event_type(@Name)", stored)).IsEqualTo(expected)
      .Because("the old name is an alias of the new one, not a second definition of the canonical form");
  }

  /// <summary>
  /// A type's versioned name, normalized in SQL, is the key <see cref="TypeNameFormatter.Format"/> writes for it;
  /// and the assembly the SQL extracts is the type's own, generic or not.
  /// </summary>
  [Test]
  [Arguments(typeof(string))]
  [Arguments(typeof(Nested))]
  [Arguments(typeof(ClrTypeNameNormalizerParityTests))]
  public async Task SqlNormalizer_OfAVersionedName_IsTheFormattedKeyAsync(Type type) {
    var versioned = TypeNameFormatter.AssemblyQualifiedName(type);

    await Assert.That(await _scalarAsync("SELECT wh_normalize_clr_type_name(@Name)", versioned))
      .IsEqualTo(TypeNameFormatter.Format(type));
  }

  [Test]
  [Arguments(typeof(string))]
  [Arguments(typeof(Nested))]
  [Arguments(typeof(Dictionary<Nested, string>))]
  [Arguments(typeof(List<Dictionary<int, Nested>>))]
  public async Task SqlAssemblyName_IsTheTypesOwnAssemblyAsync(Type type) {
    var versioned = TypeNameFormatter.AssemblyQualifiedName(type);
    var expected = type.Assembly.GetName().Name;

    await Assert.That(await _scalarAsync("SELECT wh_normalize_assembly_name(@Name)", versioned)).IsEqualTo(expected);
    await Assert.That(await _scalarAsync("SELECT normalize_assembly_name(@Name)", versioned)).IsEqualTo(expected);
  }

  /// <summary>
  /// The diagnostic finds assembly-qualified names in a consumer's own table, under a column name nothing
  /// predicted, and reports more spellings than logical types: the defect, rather than a raw count.
  /// </summary>
  [Test]
  public async Task FindQualifiedTypeNames_ReportsSpellingsPerLogicalTypeAsync() {
    await _execAsync("""
      CREATE TABLE consumer_items (id int PRIMARY KEY, settings jsonb, note text);
      INSERT INTO consumer_items VALUES
        (1, '{"handler": "Ns.Handler, Some.Assembly, Version=1.54.0.0, Culture=neutral, PublicKeyToken=null"}', 'plain'),
        (2, '{"handler": "Ns.Handler, Some.Assembly, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"}', 'plain'),
        (3, '{"handler": "Ns.Handler, Some.Assembly"}', 'Version=2 of the form, in prose');
      """);

    await using var conn = new NpgsqlConnection(_connectionString);
    var found = (await conn.QueryAsync<(string Table, string Column, long Rows, long Spellings, long Types, string Sample)>(
      "SELECT table_name, column_name, rows, distinct_spellings, logical_types, sample FROM wh_find_qualified_type_names()"))
      .ToList();

    var (_, column, rows, spellings, types, sample) = found.Single(f => f.Table == "consumer_items");
    await Assert.That(column).IsEqualTo("settings");
    await Assert.That(rows).IsEqualTo(2L);
    await Assert.That(spellings).IsEqualTo(2L);
    await Assert.That(types).IsEqualTo(1L);
    await Assert.That(sample).Contains("Ns.Handler, Some.Assembly, Version=");
  }

  /// <summary>
  /// Whizbang's own sweep normalizes the names inside perspective snapshots, which hold consumer-shaped documents
  /// and would otherwise reintroduce the decorated form on the next restore. Prose that merely mentions a version
  /// is left alone, and the pass records that it ran.
  /// </summary>
  [Test]
  public async Task SnapshotSweep_NormalizesQualifiedNamesInsideSnapshotsAsync() {
    var streamId = Guid.NewGuid();
    await _execAsync(
      "UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'perspective_snapshot_type_name_version'");
    await using (var conn = new NpgsqlConnection(_connectionString)) {
      await conn.ExecuteAsync("""
        INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number)
        VALUES (@StreamId, 'Ns.Projection', @EventId, @Data::jsonb, 1)
        """, new {
        StreamId = streamId,
        EventId = Guid.NewGuid(),
        Data = """{"key": "Ns.Box`1[[Ns.Inner, Inner.Asm, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], Some.Assembly, Version=1.54.0.0, Culture=neutral, PublicKeyToken=null", "note": "a, Version=2 of the plan"}""",
      });
    }

    long passes = 0;
    while (await _longAsync("SELECT wh_normalize_snapshot_type_names_batch(100)") > 0) {
      passes++;
    }

    var data = await _scalarAsync("SELECT snapshot_data::text FROM wh_perspective_snapshots WHERE stream_id = @Name::uuid", streamId.ToString());
    await Assert.That(data).Contains("\"key\": \"Ns.Box`1[[Ns.Inner, Inner.Asm]], Some.Assembly\"");
    await Assert.That(data).Contains("\"note\": \"a, Version=2 of the plan\"");
    await Assert.That(passes).IsGreaterThan(0L);
    await Assert.That(await _scalarAsync(
        "SELECT setting_value FROM wh_settings WHERE setting_key = @Name", "perspective_snapshot_type_name_version"))
      .IsEqualTo("1");
  }

  /// <summary>A store whose snapshots were already swept does no work on a later start.</summary>
  [Test]
  public async Task SnapshotSweep_AfterItRan_ReportsNothingToDoAsync() {
    await Assert.That(await _scalarAsync(
        "SELECT setting_value FROM wh_settings WHERE setting_key = @Name", "perspective_snapshot_type_name_version"))
      .IsEqualTo("1");
    await Assert.That(await _longAsync("SELECT wh_normalize_snapshot_type_names_batch(100)")).IsEqualTo(0L);
  }

  private async Task<string?> _scalarAsync(string sql, string name) {
    await using var conn = new NpgsqlConnection(_connectionString);
    return await conn.ExecuteScalarAsync<string?>(sql, new { Name = name });
  }

  private async Task<long> _longAsync(string sql) {
    await using var conn = new NpgsqlConnection(_connectionString);
    return await conn.ExecuteScalarAsync<long>(sql);
  }

  private async Task _execAsync(string sql) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.ExecuteAsync(sql);
  }
}
