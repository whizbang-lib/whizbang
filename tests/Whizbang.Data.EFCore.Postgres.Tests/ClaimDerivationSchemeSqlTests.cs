// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// An inbox row records the derivation scheme of the build that claims it first, in the claim's own update (#2633,
/// migration 740), and keeps it on every later claim, so a retry on any build derives the ids its first run derived. A
/// refund back to no claim un-records it, because the refunded claim never ran the row.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#a-receptor-invoked-locally</docs>
[Category("Shard3")]
public class ClaimDerivationSchemeSqlTests : EFCoreTestBase {
  [Test]
  public async Task AFirstClaim_RecordsTheCallingBuildsSchemeAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = await _registerInstanceAsync(conn);
    var id = await _seedUnclaimedInboxAsync(conn);

    await _claimAsync(conn, instance, scheme: 1);

    await Assert.That(await _attemptsAsync(conn, id)).IsEqualTo(1);
    await Assert.That(await _recordedAsync(conn, id)).IsEqualTo((short)1);
  }

  [Test]
  public async Task ACoordinatorsClaim_RecordsThisBuildsSchemeAsync() {
    // The claim the work coordinator makes hands the scheme this build runs under
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = await _registerInstanceAsync(conn);
    var id = await _seedUnclaimedInboxAsync(conn);

    _ = await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions())
      .ClaimWorkAsync(new ClaimWorkRequest(instance, "test", "test-host", 1, PartitionCount: 1));

    await Assert.That(await _recordedAsync(conn, id)).IsEqualTo((short)HandlingDerivationScheme.OwnHandling);
  }

  [Test]
  public async Task AFirstClaimByAnEarlierBuild_RecordsNothing_AndNoLaterClaimRecordsOneAsync() {
    // An earlier build passes no scheme; its first run derived the shared scheme's ids, which every retry must keep
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = await _registerInstanceAsync(conn);
    var id = await _seedUnclaimedInboxAsync(conn);

    await _claimAsync(conn, instance, scheme: null);
    await _endAttemptAsync(conn, id);
    await _claimAsync(conn, instance, scheme: 1);

    await Assert.That(await _attemptsAsync(conn, id)).IsEqualTo(2);
    await Assert.That(await _recordedAsync(conn, id)).IsNull()
      .Because("the row was claimed before, so the new build's claim is a retry and the fetch reads it as the shared scheme");
  }

  [Test]
  public async Task ARecordedScheme_IsKept_ByEveryLaterClaimAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = await _registerInstanceAsync(conn);
    var id = await _seedUnclaimedInboxAsync(conn);

    await _claimAsync(conn, instance, scheme: 1);
    await _endAttemptAsync(conn, id);
    await _claimAsync(conn, instance, scheme: null);
    await _endAttemptAsync(conn, id);
    await _claimAsync(conn, instance, scheme: 0);

    await Assert.That(await _attemptsAsync(conn, id)).IsEqualTo(3);
    await Assert.That(await _recordedAsync(conn, id)).IsEqualTo((short)1);
  }

  [Test]
  [Arguments("release_unstarted_leases")]
  [Arguments("release_unprocessed_inbox")]
  public async Task ARefundToNoClaim_UnrecordsTheScheme_SoTheRealFirstClaimRecordsItsOwnAsync(string release) {
    // A claim a refund hands back never ran the row: an earlier build that then runs it first must not inherit the stamp
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = await _registerInstanceAsync(conn);
    var id = await _seedUnclaimedInboxAsync(conn);
    await _claimAsync(conn, instance, scheme: 1);

    await _releaseAsync(conn, release, instance, id);

    await Assert.That(await _attemptsAsync(conn, id)).IsEqualTo(0);
    await Assert.That(await _recordedAsync(conn, id)).IsNull();
    await _claimAsync(conn, instance, scheme: null);
    await Assert.That(await _recordedAsync(conn, id)).IsNull()
      .Because("the earlier build's claim is the row's first, and records what it runs under: nothing, the shared scheme");
  }

  [Test]
  [Arguments("release_unstarted_leases")]
  [Arguments("release_unprocessed_inbox")]
  public async Task ARefundOfALaterClaim_KeepsTheFirstClaimsSchemeAsync(string release) {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = await _registerInstanceAsync(conn);
    var id = await _seedUnclaimedInboxAsync(conn);
    await _claimAsync(conn, instance, scheme: 1);
    await _endAttemptAsync(conn, id);
    await _claimAsync(conn, instance, scheme: 1);

    await _releaseAsync(conn, release, instance, id);

    await Assert.That(await _attemptsAsync(conn, id)).IsEqualTo(1);
    await Assert.That(await _recordedAsync(conn, id)).IsEqualTo((short)1)
      .Because("the first claim ran the row, and its scheme stands");
  }

  [Test]
  [Arguments("claim_work")]
  [Arguments("claim_orphaned_inbox")]
  [Arguments("release_unstarted_leases")]
  [Arguments("release_unprocessed_inbox")]
  [Arguments("fetch_inbox_batch_with_derivation_scheme")]
  public async Task EachFunction740Defines_HasExactlyOneOverloadAsync(string function) {
    // Widening a signature with a defaulted parameter beside the old one would make a call that matches both through
    // defaults ambiguous (42725); 740 drops every overload before it defines the new one
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE p.proname = @f AND n.nspname = current_schema()";
    cmd.Parameters.AddWithValue("f", function);

    await Assert.That(Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(1L);
  }

  [Test]
  public async Task AnEarlierBuildsClaim_InItsOwnArgumentList_ResolvesToTheOneClaimAsync() {
    // The argument lists a build before 740 sends: the EF Core coordinator's 22 positional arguments, and the Dapper
    // coordinator's 11 positional and five named ones. Each resolves to the one claim_work, and stamps nothing.
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = await _registerInstanceAsync(conn);
    var id = await _seedUnclaimedInboxAsync(conn);

    foreach (var call in new[] {
      "SELECT * FROM claim_work(@inst, 'test', 'test-host', 1, 100, 1, 300, 0.5, NULL, FALSE, NULL, FALSE, INTERVAL '30 minutes', 10, INTERVAL '4 hours', NULL, NULL, NULL, NULL, FALSE, NULL, TRUE)",
      "SELECT * FROM claim_work(@inst, 'test', 'test-host', 1, 100, 1, 300, 0.5, NULL, FALSE, NULL, p_assignment_epoch => NULL, p_assignment_revision => NULL, p_alive_lock_held => FALSE, p_lifecycle_phase => NULL, p_acquire_inbox => TRUE)",
    }) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = call;
      cmd.Parameters.AddWithValue("inst", instance);
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // drain
      }
    }

    await Assert.That(await _attemptsAsync(conn, id)).IsEqualTo(1).Because("the first call claimed the row; the second found it leased");
    await Assert.That(await _recordedAsync(conn, id)).IsNull();
  }

  // ===== Helpers =====

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task<Guid> _registerInstanceAsync(NpgsqlConnection conn) {
    var instance = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())
      """;
    cmd.Parameters.AddWithValue("inst", instance);
    await cmd.ExecuteNonQueryAsync();
    return instance;
  }

  private static async Task<Guid> _seedUnclaimedInboxAsync(NpgsqlConnection conn) {
    var id = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
        VALUES (@id, 'TestHandler', 'TestCommand', '{}', '{}', NOW() - INTERVAL '1 second', gen_random_uuid())
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts,
         partition_number, instance_id, lease_expiry, error, failure_reason)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, 0, NULL, NULL, NULL, 99
      FROM m
      """;
    cmd.Parameters.AddWithValue(nameof(id), id);
    await cmd.ExecuteNonQueryAsync();
    return id;
  }

  /// <summary>One claim poll; a null scheme is a build before 740, which leaves the parameter out entirely.</summary>
  private static async Task _claimAsync(NpgsqlConnection conn, Guid instance, short? scheme) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = scheme is null
      ? "SELECT * FROM claim_work(@inst, 'test', 'test-host', 1, 100, 1, 300)"
      : "SELECT * FROM claim_work(@inst, 'test', 'test-host', 1, 100, 1, 300, p_derivation_scheme => @scheme)";
    cmd.Parameters.AddWithValue("inst", instance);
    if (scheme is { } value) {
      cmd.Parameters.AddWithValue(nameof(scheme), value);
    }
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // drain
    }
  }

  private static async Task _endAttemptAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    // The attempt ended as a failure does: the lease released, the attempt it spent kept, so the next poll claims the row
    // again (a lease that merely ran out on a stream the instance still owns is renewed without a claim, 462)
    cmd.CommandText = "UPDATE wh_inbox_state SET instance_id = NULL, lease_expiry = NULL WHERE message_id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _releaseAsync(NpgsqlConnection conn, string release, Guid instance, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = release == "release_unstarted_leases"
      ? "SELECT * FROM release_unstarted_leases(@inst, ARRAY[(SELECT stream_id FROM wh_inbox_state WHERE message_id = @id)], NULL)"
      : "SELECT release_unprocessed_inbox(@inst, ARRAY[@id])";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue(nameof(id), id);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // drain
    }
  }

  private static async Task<int> _attemptsAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT attempts FROM wh_inbox_state WHERE message_id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<short?> _recordedAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT derivation_scheme FROM wh_inbox_state WHERE message_id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    var value = await cmd.ExecuteScalarAsync();
    return value is DBNull or null ? null : (short)value;
  }
}
