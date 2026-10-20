// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// <see cref="DapperWorkCoordinator.GetStateBasedStreamIdsAsync"/> names the streams a rebuild or rewind must refuse
/// (#1538): a stream holding an ephemeral or a compacted event is state-based, so its event log is not a source of
/// truth. The Dapper coordinator used to inherit the default, which names none, so a rebuild on Dapper replayed
/// ephemeral streams whose bodies were already reaped.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
[NotInParallel("PostgreSQL")]
[Category("Integration")]
public class DapperStateBasedStreamsTests : PostgresTestBase {
  private const int EPHEMERAL = 8;
  private const int COMPACTED = 16;

  [Test]
  public async Task GetStateBasedStreamIdsAsync_NamesTheEphemeralAndCompactedStreamsAskedAboutAsync() {
    var ephemeral = Guid.CreateVersion7();
    var compacted = Guid.CreateVersion7();
    var sourced = Guid.CreateVersion7();
    var notAsked = Guid.CreateVersion7();
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await _eventAsync(conn, ephemeral, version: 1, flags: 0);
      await _eventAsync(conn, ephemeral, version: 2, flags: EPHEMERAL);
      await _eventAsync(conn, compacted, version: 1, flags: COMPACTED);
      await _eventAsync(conn, sourced, version: 1, flags: 0);
      await _eventAsync(conn, notAsked, version: 1, flags: EPHEMERAL);
    }
    var coordinator = new DapperWorkCoordinator(ConnectionString, new JsonSerializerOptions(), NullLogger<DapperWorkCoordinator>.Instance);

    var stateBased = await coordinator.GetStateBasedStreamIdsAsync([ephemeral, compacted, sourced]);

    await Assert.That(stateBased).IsEquivalentTo([ephemeral, compacted])
      .Because("One ephemeral or compacted event makes a stream state-based; a stream not asked about is not named.");
  }

  [Test]
  public async Task GetStateBasedStreamIdsAsync_NoStreams_NamesNoneAsync() {
    var coordinator = new DapperWorkCoordinator(ConnectionString, new JsonSerializerOptions(), NullLogger<DapperWorkCoordinator>.Instance);

    var stateBased = await coordinator.GetStateBasedStreamIdsAsync([]);

    await Assert.That(stateBased).IsEmpty();
  }

  private static async Task _eventAsync(NpgsqlConnection conn, Guid stream, int version, int flags) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, flags, created_at)
      VALUES (@id, @stream, @stream, 'TestAgg', @version, 'TestEvt', @flags, NOW())
      """;
    ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
    ins.Parameters.AddWithValue("stream", stream);
    ins.Parameters.AddWithValue("version", version);
    ins.Parameters.AddWithValue("flags", flags);
    await ins.ExecuteNonQueryAsync();
  }
}
