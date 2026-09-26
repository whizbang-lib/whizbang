using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Npgsql;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// The Dapper store writes a perspective's physical columns, on insert and on update, so a query or an
/// index that reads a promoted property from its column sees the value rather than NULL.
/// </summary>
[NotInParallel("PostgreSQL")]
public class DapperPerspectiveStorePhysicalFieldTests : PostgresTestBase {
  private const string TABLE_NAME = "wh_per_dapper_physical";
  private JsonSerializerOptions _jsonOptions = null!;

  public enum Tier { Bronze, Gold }

  /// <summary>A value the database parses from its text form, the way a vector is written.</summary>
  public readonly record struct Point(int X, int Y) {
    public override string ToString() => $"({X},{Y})";
  }

  internal sealed class PhysicalModel {
    public string Name { get; set; } = "";
  }

  [Before(Test)]
  public async Task CreateTableAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand($"""
      DROP TABLE IF EXISTS {TABLE_NAME};
      CREATE TABLE {TABLE_NAME} (
        id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL DEFAULT '{"{}"}'::jsonb,
        scope JSONB NOT NULL DEFAULT '{"{}"}'::jsonb, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), version INT NOT NULL DEFAULT 1,
        name TEXT, amount DECIMAL, placed_at TIMESTAMPTZ, tier TEXT, location POINT, owner_id UUID);
      """, conn);
    await cmd.ExecuteNonQueryAsync();
    _jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        DapperPhysicalFieldTestJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
    };
  }

  private DapperPostgresPerspectiveStore<PhysicalModel> _store() => new(ConnectionString, TABLE_NAME, _jsonOptions);

  private static Dictionary<string, object?> _values(string name, decimal amount, DateTimeOffset placedAt, Tier tier, Point location, Guid? owner) => new() {
    ["name"] = name,
    ["amount"] = amount,
    ["placed_at"] = placedAt,
    ["tier"] = tier,
    ["location"] = location,
    ["owner_id"] = owner,
  };

  private async Task<(string? Name, decimal? Amount, DateTimeOffset? PlacedAt, string? Tier, string? Location, Guid? Owner, string? EventType)> _readAsync(Guid id) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      $"SELECT name, amount, placed_at, tier, location::text, owner_id, metadata ->> 'EventType' FROM {TABLE_NAME} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    async Task<T?> field<T>(int ordinal) => await reader.IsDBNullAsync(ordinal) ? default : await reader.GetFieldValueAsync<T>(ordinal);
    return (await field<string>(0), await field<decimal?>(1), await field<DateTimeOffset?>(2), await field<string>(3),
      await field<string>(4), await field<Guid?>(5), await field<string>(6));
  }

  [Test]
  public async Task Upsert_Insert_WritesEveryPhysicalColumnAsync() {
    var id = Guid.CreateVersion7();
    var owner = Guid.CreateVersion7();
    var placedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(-5));

    await _store().UpsertWithPhysicalFieldsAsync(id, new PhysicalModel { Name = "a" },
      _values("first", 12.5m, placedAt, Tier.Gold, new Point(1, 2), owner), new PerspectiveScope());

    var row = await _readAsync(id);
    await Assert.That(row.Name).IsEqualTo("first");
    await Assert.That(row.Amount).IsEqualTo(12.5m);
    await Assert.That(row.PlacedAt).IsEqualTo(placedAt)
      .Because("an offset instant is stored as the same instant");
    await Assert.That(row.Tier).IsEqualTo("Gold");
    await Assert.That(row.Location).IsEqualTo("(1,2)")
      .Because("a value the driver cannot send natively (a vector) is sent as text and parsed by the column's type");
    await Assert.That(row.Owner).IsEqualTo(owner);
  }

  [Test]
  public async Task Upsert_Update_RewritesThePhysicalColumnsAsync() {
    var id = Guid.CreateVersion7();
    var placedAt = DateTimeOffset.UtcNow;
    await _store().UpsertWithPhysicalFieldsAsync(id, new PhysicalModel(),
      _values("first", 1m, placedAt, Tier.Bronze, new Point(1, 1), Guid.CreateVersion7()), new PerspectiveScope());

    await _store().UpsertWithPhysicalFieldsAsync(id, new PhysicalModel(),
      _values("second", 2m, placedAt, Tier.Gold, new Point(3, 4), null), new PerspectiveScope(), forceUpdateScope: false);

    var row = await _readAsync(id);
    await Assert.That(row.Name).IsEqualTo("second");
    await Assert.That(row.Amount).IsEqualTo(2m);
    await Assert.That(row.Tier).IsEqualTo("Gold");
    await Assert.That(row.Location).IsEqualTo("(3,4)");
    await Assert.That(row.Owner).IsNull().Because("a property that became null clears its column");
  }

  [Test]
  public async Task Upsert_WithMetadata_KeepsTheMetadataAndWritesTheColumnsAsync() {
    var id = Guid.CreateVersion7();
    var store = _store();

    await store.UpsertWithPhysicalFieldsAsync(id, new PhysicalModel(),
      _values("m", 1m, DateTimeOffset.UtcNow, Tier.Gold, new Point(0, 0), null), new PerspectiveScope(), false,
      new PerspectiveMetadata { EventType = "Ns.Placed, App", EventId = Guid.CreateVersion7().ToString(), Timestamp = DateTime.UtcNow });

    var row = await _readAsync(id);
    await Assert.That(row.Name).IsEqualTo("m");
    await Assert.That(row.EventType).IsEqualTo("Ns.Placed, App")
      .Because("the applied event's metadata is persisted on the physical-field path too");
  }

  [Test]
  public async Task Upsert_AMixedCaseColumnName_IsTheSameUnquotedIdentifierTheTableDeclaresAsync() {
    var id = Guid.CreateVersion7();

    await _store().UpsertWithPhysicalFieldsAsync(id, new PhysicalModel(), new Dictionary<string, object?> { ["Name"] = "folded" });

    await Assert.That((await _readAsync(id)).Name).IsEqualTo("folded");
  }

  [Test]
  public async Task Upsert_WithNoPhysicalValues_WritesTheDocumentOnlyAsync() {
    var id = Guid.CreateVersion7();

    await _store().UpsertWithPhysicalFieldsAsync(id, new PhysicalModel { Name = "doc" }, new Dictionary<string, object?>());

    var row = await _readAsync(id);
    await Assert.That(row.Name).IsNull();
  }

  [Test]
  [Arguments("name; DROP TABLE x")]
  [Arguments("1name")]
  [Arguments("na-me")]
  [Arguments("")]
  public async Task Upsert_AColumnNameThatIsNotAPlainIdentifier_IsRefusedAsync(string column) {
    var values = new Dictionary<string, object?> { [column] = "x" };

    await Assert.That(() => _store().UpsertWithPhysicalFieldsAsync(Guid.CreateVersion7(), new PhysicalModel(), values))
      .Throws<ArgumentException>();
  }
}

[JsonSerializable(typeof(DapperPerspectiveStorePhysicalFieldTests.PhysicalModel))]
[JsonSerializable(typeof(PerspectiveScope))]
internal sealed partial class DapperPhysicalFieldTestJsonContext : JsonSerializerContext;
