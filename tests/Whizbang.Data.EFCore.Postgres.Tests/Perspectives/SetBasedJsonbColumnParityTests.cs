// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The one writer rule for promoted columns (#2246): a set-based write stores the document and every physical column
/// (jsonb documents written through their own converter, a timestamp, nulls) with the bytes the per-stream write stores,
/// on insert and on an update on the read version.
/// </summary>
/// <remarks>The generated context of <see cref="JsonbExtractedItem"/>, so the columns' converters are the generated ones.</remarks>
/// <docs>fundamentals/perspectives/write-modes</docs>
[Category("Integration")]
[Category("Shard3")]
public class SetBasedJsonbColumnParityTests {
  private const string TABLE = "wh_per_jsonb_extracted_item";
  private static readonly DateTime _eventAt = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("sink_jsonb");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await using var context = PhysicalJsonbContainmentIntegrationTests.Context(_connectionString);
    await context.EnsureWhizbangDatabaseInitializedAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      NpgsqlConnection.ClearPool(new NpgsqlConnection(_connectionString));
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  [Test]
  public async Task JsonbAndTimestampColumns_AreByteIdenticalToTheSingleRowWriteAsync() {
    var id = Guid.Parse("01890000-0000-7000-8000-000000002246");
    var single = await _writeTwiceAsync(id, useSink: false);
    await _executeAsync($"DELETE FROM {TABLE} WHERE id = '{id}'");
    var sink = await _writeTwiceAsync(id, useSink: true);

    await Assert.That(sink.Inserted).IsEqualTo(single.Inserted);
    await Assert.That(sink.Updated).IsEqualTo(single.Updated);
    await Assert.That(single.Inserted).Contains("\"north\"")
      .Because("the snapshot holds the promoted columns, so equal snapshots compare the columns and not only the document");
  }

  private async Task<(string Inserted, string Updated)> _writeTwiceAsync(Guid id, bool useSink) {
    var first = new JsonbExtractedItem.Model {
      Id = id,
      Title = "first",
      GridFilter = new() { ["region"] = ["north", "east"] },
      Labels = [new JsonbLabel("k", "v")],
      Tags = ["a", "b"],
      Place = new JsonbPlace { City = "Springfield", Zone = 4 },
      Stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
    };
    await _writeAsync(first, 1, PerspectiveRowVersion.Absent, useSink);
    var inserted = await _snapshotAsync(id);
    var second = new JsonbExtractedItem.Model {
      Id = id,
      Title = "second",
      GridFilter = [],
      Labels = [],
      Tags = ["c"],
      Place = null,
      Stamp = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
    };
    await _writeAsync(second, 2, PerspectiveRowVersion.Of(await _xminAsync(id)), useSink);
    return (inserted, await _snapshotAsync(id));
  }

  private async Task _writeAsync(JsonbExtractedItem.Model model, long sequence, PerspectiveRowVersion expected, bool useSink) {
    await using var context = PhysicalJsonbContainmentIntegrationTests.Context(_connectionString);
    var strategy = new PostgresUpsertStrategy();
    var metadata = new PerspectiveMetadata {
      EventType = "JsonbItemNoted",
      EventId = "019f0002-0000-7aaa-8000-000000000001",
      Timestamp = _eventAt,
      CommitSequence = sequence,
    };
    var physical = new Dictionary<string, object?> {
      ["grid_filter"] = model.GridFilter,
      ["labels"] = model.Labels,
      ["tags"] = model.Tags,
      ["place"] = model.Place,
      ["stamp"] = model.Stamp,
    };
    if (useSink) {
      var write = strategy.PreparePendingWrite(context, TABLE, model.Id, model, metadata, new PerspectiveScope(), physical, false, expected)!;
      var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([write]);
      await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Written);
    } else {
      await Assert.That(await strategy.TryUpsertPerspectiveRowWithPhysicalFieldsAsync(
        context, TABLE, model.Id, model, metadata, new PerspectiveScope(), physical, false, expected)).IsTrue();
    }
  }

  private async Task<string> _snapshotAsync(Guid id) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT concat_ws(' | ', data::text, metadata::text, scope::text, version::text, created_at::text, updated_at::text, " +
      $"grid_filter::text, labels::text, tags::text, coalesce(place::text, '<null>'), stamp::text) FROM {TABLE} WHERE id = @id", connection);
    command.Parameters.AddWithValue(nameof(id), id);
    return (string)(await command.ExecuteScalarAsync())!;
  }

  private async Task<long> _xminAsync(Guid id) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand($"SELECT xmin::text::bigint FROM {TABLE} WHERE id = @id", connection);
    command.Parameters.AddWithValue(nameof(id), id);
    return (long)(await command.ExecuteScalarAsync())!;
  }

  private async Task _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
  }
}
