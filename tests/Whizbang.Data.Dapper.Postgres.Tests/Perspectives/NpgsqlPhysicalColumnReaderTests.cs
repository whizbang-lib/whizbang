using Npgsql;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// The Dapper store reads a Split model's promoted enum back from its column. The column holds the member's
/// underlying number, as the store writes it, in whichever numeric type the enum's underlying type maps to; a
/// column an earlier release created as text still holds the member's name, and reads back from it.
/// </summary>
/// <tests>src/Whizbang.Data.Dapper.Postgres/NpgsqlPhysicalColumnReader.cs</tests>
[NotInParallel("PostgreSQL")]
public class NpgsqlPhysicalColumnReaderTests : PostgresTestBase {
  public enum Tier { None, Gold }

  public enum WideTier : ulong { None = 0, Top = ulong.MaxValue }

  private async Task<T> _readAsync<T>(string columnSql) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand($"SELECT '{{}}'::jsonb, {columnSql} AS tier", connection);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return new NpgsqlPhysicalColumnReader(reader, [new SplitPhysicalColumn("tier", IsVector: false)]).Read<T>("tier");
  }

  [Test]
  public async Task Read_AnIntegerEnumColumn_ReturnsTheMemberAsync() {
    await Assert.That(await _readAsync<Tier>("1::integer")).IsEqualTo(Tier.Gold);
  }

  [Test]
  public async Task Read_ASmallintEnumColumn_ReturnsTheMemberOfTheNullableAsync() {
    await Assert.That(await _readAsync<Tier?>("1::smallint")).IsEqualTo(Tier.Gold);
  }

  [Test]
  public async Task Read_ANumericColumnOfAUlongEnum_ReturnsTheMemberAsync() {
    await Assert.That(await _readAsync<WideTier>("18446744073709551615::numeric")).IsEqualTo(WideTier.Top);
  }

  [Test]
  public async Task Read_ATextColumnHoldingTheName_ParsesItAsync() {
    await Assert.That(await _readAsync<Tier>("'Gold'::text")).IsEqualTo(Tier.Gold)
      .Because("a column an earlier release created as text holds the member's name");
  }
}
