// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2255 on the Dapper driver: the coordinator syncs the pairs allowed to share a stream into the store, and a rebuild
/// that passes over an allowed ephemeral type finds the mixed stream rebuildable, while one that does not still refuses it.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
[NotInParallel("PostgreSQL")]
[Category("Integration")]
public class DapperMixedStreamAllowanceTests : PostgresTestBase {
  private const int EPHEMERAL = 8;
  private static readonly string _pinged = TypeNameFormatter.Format(typeof(DapperMixPinged));
  private static readonly string _opened = TypeNameFormatter.Format(typeof(DapperMixOpened));
  private static readonly string _typed = TypeNameFormatter.Format(typeof(DapperMixTyped));

  [Test]
  public async Task SyncMixedStreamAllowancesAsync_AllowsThePair_AndAnEmptyListAllowsNothingAsync() {
    var coordinator = _coordinator();
    var stream = (Guid)TrackedGuid.New();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _appendAsync(conn, stream, _opened, 0);

    await ((IWorkCoordinator)coordinator).SyncMixedStreamAllowancesAsync([new MixedStreamPair(_pinged, _opened)]);
    await _appendAsync(conn, stream, _pinged, EPHEMERAL);
    await ((IWorkCoordinator)coordinator).SyncMixedStreamAllowancesAsync([]);

    await Assert.That(await _countAsync(conn, stream)).IsEqualTo(2L).Because("the allowed pair shared the stream");
    var refusal = await Assert.That(() => _appendAsync(conn, stream, _pinged, EPHEMERAL)).Throws<PostgresException>();
    await Assert.That(refusal!.SqlState).IsEqualTo("WH133").Because("an empty sync clears the allowance, and mixing is refused again");
  }

  [Test]
  public async Task GetStateBasedStreamIdsAsync_PassingOverTheAllowedType_LeavesTheMixedStreamRebuildableAsync() {
    var coordinator = _coordinator();
    var mixed = (Guid)TrackedGuid.New();
    var plain = (Guid)TrackedGuid.New();
    await ((IWorkCoordinator)coordinator).SyncMixedStreamAllowancesAsync([new MixedStreamPair(_pinged, _opened)]);
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await _appendAsync(conn, mixed, _opened, 0);
      await _appendAsync(conn, mixed, _pinged, EPHEMERAL);
      await _appendAsync(conn, plain, _typed, EPHEMERAL);
    }

    var passingOver = await ((IWorkCoordinator)coordinator).GetStateBasedStreamIdsAsync([mixed, plain], [_pinged]);
    var not = await ((IWorkCoordinator)coordinator).GetStateBasedStreamIdsAsync([mixed, plain], []);

    await Assert.That(passingOver).IsEquivalentTo([plain]);
    await Assert.That(not).IsEquivalentTo([mixed, plain]);
    await Assert.That(await ((IWorkCoordinator)coordinator).GetStateBasedStreamIdsAsync([], [_pinged])).IsEmpty();
  }

  private DapperWorkCoordinator _coordinator() =>
    new(ConnectionString, new JsonSerializerOptions(), NullLogger<DapperWorkCoordinator>.Instance);

  /// <summary>An event through the direct append path, under the store's mixed-stream refusal.</summary>
  private static async Task _appendAsync(NpgsqlConnection conn, Guid stream, string type, int flags) {
    var id = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_append_event(@id, @s, @s, split_part(@t, ',', 1), @t, '{\"OrderId\":42}'::jsonb, @m::jsonb, NULL, NOW(), @f)";
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("t", type);
    cmd.Parameters.AddWithValue("m", $$"""{"MessageId":"{{id}}","Hops":[]}""");
    cmd.Parameters.AddWithValue("f", flags);
    _ = await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_event_store WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", stream);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }
}

/// <summary>A real ephemeral event type allowed to share a stream with <see cref="DapperMixOpened"/>.</summary>
internal sealed record DapperMixPinged;

/// <summary>A real Sourced event type paired with <see cref="DapperMixPinged"/>.</summary>
internal sealed record DapperMixOpened;

/// <summary>A real ephemeral event type paired with nothing.</summary>
internal sealed record DapperMixTyped;
