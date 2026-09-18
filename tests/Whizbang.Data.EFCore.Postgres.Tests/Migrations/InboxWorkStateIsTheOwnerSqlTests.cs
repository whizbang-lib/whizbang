using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The rewritten claim-state functions write <c>wh_inbox_state</c>, asserted by reading that table
/// and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the batch verification on its own proves less than it appears to. While the
/// split is being built, a sync trigger keeps <c>wh_inbox</c>'s mutable columns and the lease table
/// in step so each batch can be checked against the existing suite. That scaffold also makes the
/// existing suite LESS discriminating: a test that asserts on <c>wh_inbox.instance_id</c> passes
/// whether the function wrote the lease table and the trigger copied it back, or wrote
/// <c>wh_inbox</c> directly as before. Those tests confirm the logic still produces the same
/// observable state transition, which is worth having and is not the same as confirming the rewrite.
/// </para>
/// <para>
/// So these assertions read <c>wh_inbox_state</c> directly, and it is worth being exact about what
/// that does and does not establish while the scaffold is in place. It does NOT prove the functions
/// target the lease table: if one were left writing <c>wh_inbox</c>, the trigger would copy the
/// value across and these assertions would still pass. **No runtime test can discriminate while a
/// bidirectional scaffold makes the two representations equal by construction**, and claiming
/// otherwise would be the same mistake as a gate whose fixture leaves its table empty.
/// </para>
/// <para>
/// What they establish now is that the rewritten functions still compute the right values: the
/// attempt refund and its floor, the renewal's direction and size, and the live-lease-only rule for
/// the budget. That is behavior preservation, which is what a batch is verifying.
/// </para>
/// <para>
/// Discrimination arrives when the scaffold and the columns are removed, and these become the
/// permanent verification at that point, because the lease table is then the only place this state
/// exists and every assertion here still reads. The existing tests that assert on
/// <c>wh_inbox.instance_id</c> will fail then and have to move; that failure is the real proof, and
/// it is expected rather than a regression.
/// </para>
/// </remarks>
[Category("Integration")]
[Category("Shard3")]
public class InboxWorkStateIsTheOwnerSqlTests : EFCoreTestBase {
  [Test]
  [Timeout(600000)]
  public async Task ReleaseUnprocessedInbox_RefundsTheAttemptInTheLeaseTableAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var instance = Guid.NewGuid();
    var message = await _seedLeasedInboxRowAsync(conn, instance, attempts: 3, cancellationToken);

    await _execAsync(conn,
      "SELECT release_unprocessed_inbox(@inst, ARRAY[@msg]::uuid[])",
      cancellationToken, ("inst", instance), ("msg", message));

    // Read the lease table alone. attempts is the discriminating value: the function computes
    // GREATEST(attempts - 1, 0), so 2 can only appear if this function ran against this table.
    var state = await _leaseStateAsync(conn, message, cancellationToken);
    await Assert.That(state.Attempts).IsEqualTo(2)
      .Because("the graceful release refunds the optimistic claim attempt, and the lease table is "
        + "where the attempt count lives after the split");
    await Assert.That(state.HasInstance).IsFalse()
      .Because("a released row has to be immediately claimable again rather than invisible until "
        + "the lease it relinquished would have expired");
  }

  [Test]
  [Timeout(600000)]
  public async Task ReleaseUnprocessedInbox_NeverDrivesTheAttemptCountNegativeAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var instance = Guid.NewGuid();
    var message = await _seedLeasedInboxRowAsync(conn, instance, attempts: 0, cancellationToken);

    await _execAsync(conn,
      "SELECT release_unprocessed_inbox(@inst, ARRAY[@msg]::uuid[])",
      cancellationToken, ("inst", instance), ("msg", message));

    var state = await _leaseStateAsync(conn, message, cancellationToken);
    await Assert.That(state.Attempts).IsEqualTo(0)
      .Because("a negative attempt budget would make the row un-dead-letterable, trading a double "
        + "refund for an unbounded retry, so the floor is part of the behavior rather than a detail");
  }

  [Test]
  [Timeout(600000)]
  public async Task RenewLeases_MovesTheExpiryInTheLeaseTableAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var instance = Guid.NewGuid();
    var message = await _seedLeasedInboxRowAsync(conn, instance, attempts: 1, cancellationToken);

    // Renewing to half an hour from a five minute lease: the assertion is on the direction and the
    // size of the move, so it cannot pass on a row whose lease was simply left alone.
    await _execAsync(conn,
      "SELECT renew_leases('inbox', ARRAY[@msg]::uuid[], 1800)",
      cancellationToken, ("msg", message));

    var remaining = await _scalarDoubleAsync(conn,
      "SELECT EXTRACT(EPOCH FROM (lease_expiry - NOW())) FROM wh_inbox_state WHERE message_id = @msg",
      cancellationToken, ("msg", message));
    await Assert.That(remaining).IsGreaterThan(1500d)
      .Because("a renewal to 1,800 seconds has to leave substantially more than the 300 seconds the "
        + "row was seeded with, read from the table that owns the lease");
  }

  [Test]
  [Timeout(600000)]
  public async Task CountOutstandingWork_CountsFromTheLeaseTableAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var instance = Guid.NewGuid();
    _ = await _seedLeasedInboxRowAsync(conn, instance, attempts: 1, cancellationToken);
    var expired = await _seedLeasedInboxRowAsync(conn, instance, attempts: 1, cancellationToken);
    // An expired lease is no longer this instance's work, whoever still has the row stamped.
    await _execAsync(conn,
      "UPDATE wh_inbox_state SET lease_expiry = NOW() - INTERVAL '1 minute' WHERE message_id = @msg",
      cancellationToken, ("msg", expired));

    var held = await _scalarLongAsync(conn,
      "SELECT inbox_rows FROM count_outstanding_work(@inst)", cancellationToken, ("inst", instance));

    await Assert.That(held).IsEqualTo(1L)
      .Because("the budget counts live leases only: counting the expired one would hold the budget "
        + "closed against work the store has already made available to every other instance");
  }

  private readonly record struct LeaseState(int Attempts, bool HasInstance);

  private static async Task<LeaseState> _leaseStateAsync(
      NpgsqlConnection conn, Guid message, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      "SELECT attempts, instance_id IS NOT NULL FROM wh_inbox_state WHERE message_id = @msg";
    cmd.Parameters.AddWithValue("msg", message);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken)) {
      throw new InvalidOperationException(
        "no wh_inbox_state row for the seeded message: a message with no lease row can never be "
        + "claimed, so this is the invariant failing rather than the assertion");
    }
    return new LeaseState(reader.GetInt32(0), reader.GetBoolean(1));
  }

  /// <summary>
  /// One inbox message, leased by <paramref name="instance"/> with the attempt count given. Written
  /// through wh_inbox so the row and its lease are created the way the product creates them.
  /// </summary>
  private static async Task<Guid> _seedLeasedInboxRowAsync(
      NpgsqlConnection conn, Guid instance, int attempts, CancellationToken cancellationToken) {
    var message = Guid.NewGuid();
    await _execAsync(conn, @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, scope, stream_id,
         partition_number, is_event, status, attempts, received_at, source_service_id,
         source_commit_sequence, priority, instance_id, lease_expiry)
      VALUES (@msg, 'TestHandler', 'TestEvent', '{}'::jsonb, '{}'::jsonb, '{}'::jsonb,
              @stream, 1, TRUE, 0, @attempts, NOW(), @svc, 1, 150,
              @inst, NOW() + INTERVAL '5 minutes')",
      cancellationToken,
      ("msg", message), ("stream", Guid.NewGuid()), ("attempts", attempts),
      ("svc", Guid.NewGuid()), ("inst", instance));
    return message;
  }

  private static async Task _execAsync(
      NpgsqlConnection conn, string sql, CancellationToken cancellationToken,
      params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    cmd.CommandTimeout = 120;
    _ = await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private static async Task<long> _scalarLongAsync(
      NpgsqlConnection conn, string sql, CancellationToken cancellationToken,
      params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (long?)await cmd.ExecuteScalarAsync(cancellationToken) ?? -1L;
  }

  private static async Task<double> _scalarDoubleAsync(
      NpgsqlConnection conn, string sql, CancellationToken cancellationToken,
      params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var raw = await cmd.ExecuteScalarAsync(cancellationToken);
    return raw is null ? -1d : Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
  }
}
