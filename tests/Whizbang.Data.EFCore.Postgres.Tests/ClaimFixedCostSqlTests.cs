// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The claim runs only the statements that can find something (367): a re-offer walk runs only for a lane the instance
/// holds a row in, the inbox store pass stops after its lock pass when it has no candidate, and the collective routing
/// insert runs only when the pass stored a collective event. What each case offers, stores and stamps is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <c>claim_work</c> runs under <c>plan_cache_mode = force_custom_plan</c>, and a pooled connection is reset between
/// calls, so every statement the claim executes is planned on every call, once per execution: the inbox re-offer walk
/// ran eight times per claim (four buckets, two kinds) and the perspective walk up to four, each planned afresh even
/// when its lane was empty, which on a busy service was most of them. A statement that does not run is not planned.
/// </para>
/// <para>
/// The statements a claim ran are read from auto_explain notices on the test's own connection (nested statements), so
/// the counts do not depend on the machine. Each claim is rolled back unless the case needs its effect.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/367_TheClaimRunsOnlyWhatCanFindWork.sql</code-under-test>
/// <docs>fundamentals/work-coordinator/claim-loop#the-claim-runs-only-what-can-find-work</docs>
[Category("Integration")]
[Category("Shard5")]
public class ClaimFixedCostSqlTests : EFCoreTestBase {
  private const string INBOX_WALK = "WITH RECURSIVE lanes";
  private const string PERSPECTIVE_WALK = "WITH RECURSIVE lane AS";
  private const string STORE_STATEMENT = "WITH inbox_events AS";
  private const string COLLECTIVE_INSERT = "c_collective_sink";
  private const string EVENT_TYPE = "Cost.Thing, Cost";

  // ---------------------------------------------------------------------------------------------
  // The re-offer walks
  // ---------------------------------------------------------------------------------------------

  [Test]
  [Arguments(50, false)]
  [Arguments(50, true)]
  [Arguments(150, false)]
  [Arguments(150, true)]
  [Arguments(250, false)]
  [Arguments(250, true)]
  [Arguments(450, false)]
  [Arguments(450, true)]
  public async Task InboxReOffer_AnInstanceHoldingOneLane_WalksOnlyThatLane_AndOffersItsStreamAsync(int priority, bool isEvent) {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var instance = Guid.NewGuid();
    await _registerAsync(conn, instance);
    var stream = Guid.NewGuid();
    await _seedInboxAsync(conn, Guid.NewGuid(), stream, isEvent, priority, instance, stamped: true);

    var claim = await _claimAsync(conn, instance, commit: false);

    await Assert.That(claim.Offered("inbox")).IsEquivalentTo([stream])
      .Because("the one stream the instance holds is offered whichever lane it is in");
    await Assert.That(claim.Ran(INBOX_WALK)).IsEqualTo(1)
      .Because("only the lane holding a row is walked; an empty lane's walk finds nothing and costs a plan");
  }

  [Test]
  public async Task InboxReOffer_ALaneHoldingOnlyUnfetchableRows_IsStillWalked_AndOffersNothingAsync() {
    // The lane test is a superset of the walk's own predicate: a held event the store pass left unstored still counts
    // as held, so the walk runs exactly as before and its own predicate passes over the row (349).
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var instance = Guid.NewGuid();
    await _registerAsync(conn, instance);
    await _seedHeldBackEventAsync(conn, instance);

    var claim = await _claimAsync(conn, instance, commit: false);

    await Assert.That(claim.Offered("inbox")).IsEmpty();
    await Assert.That(claim.Ran(INBOX_WALK)).IsEqualTo(1);
  }

  [Test]
  public async Task Claim_AnInstanceHoldingNothing_RunsNoReOfferWalkAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var instance = Guid.NewGuid();
    var peer = Guid.NewGuid();
    await _registerAsync(conn, instance);
    await _registerAsync(conn, peer);
    // A peer's live lease: there is work in the store, so the claim does not take its empty short-circuit, but none of
    // it is this instance's to acquire or re-offer.
    await _seedInboxAsync(conn, Guid.NewGuid(), Guid.NewGuid(), isEvent: false, priority: 150, peer, stamped: true);
    await _seedPerspectiveEventAsync(conn, Guid.NewGuid(), priority: 150, peer);

    var claim = await _claimAsync(conn, instance, commit: false);

    await Assert.That(claim.Rows).IsEqualTo(0);
    await Assert.That(claim.Ran(INBOX_WALK)).IsEqualTo(0);
    await Assert.That(claim.Ran(PERSPECTIVE_WALK)).IsEqualTo(0);
  }

  [Test]
  [Arguments(50, false)]
  [Arguments(150, false)]
  [Arguments(250, false)]
  [Arguments(450, true)]
  public async Task PerspectiveReOffer_AnInstanceHoldingOneBucket_WalksOnlyThatBucket_AndOffersItsStreamAsync(
      int priority, bool idleSettled) {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var instance = Guid.NewGuid();
    await _registerAsync(conn, instance);
    var stream = Guid.NewGuid();
    await _seedPerspectiveEventAsync(conn, stream, priority, instance);

    var claim = await _claimAsync(conn, instance, commit: false, idleSettled: idleSettled);

    await Assert.That(claim.Offered("perspective_stream")).IsEquivalentTo([stream]);
    await Assert.That(claim.Ran(PERSPECTIVE_WALK)).IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveReOffer_TheIdleBucketWhileWithheld_IsNotOffered_AndNotWalkedAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var instance = Guid.NewGuid();
    await _registerAsync(conn, instance);
    await _seedPerspectiveEventAsync(conn, Guid.NewGuid(), priority: 450, instance);

    var claim = await _claimAsync(conn, instance, commit: false, idleSettled: false);

    await Assert.That(claim.Offered("perspective_stream")).IsEmpty()
      .Because("a held idle-band event is withheld while the service is busy and its wait is short (167)");
    await Assert.That(claim.Ran(PERSPECTIVE_WALK)).IsEqualTo(0);
  }

  // ---------------------------------------------------------------------------------------------
  // The inbox store pass
  // ---------------------------------------------------------------------------------------------

  [Test]
  public async Task StorePass_ARowWhoseEventIsAlreadyStored_IsStamped_WithoutRunningTheCopyAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var instance = Guid.NewGuid();
    await _registerAsync(conn, instance);
    var message = Guid.NewGuid();
    var stream = Guid.NewGuid();
    await _seedInboxAsync(conn, message, stream, isEvent: true, priority: 150, instance, stamped: false);
    await _claimAsync(conn, instance, commit: true);
    await Assert.That(await _scalarAsync<long>(conn, $"SELECT count(*) FROM wh_event_store WHERE event_id = '{message}'"))
      .IsEqualTo(1).Because("precondition: the first claim stored the event");
    // The state a pass that stored the event and was not the one to stamp it leaves behind: stored, not yet stamped.
    await _execAsync(conn, $"UPDATE wh_inbox_state SET chain_emitted_at = NULL WHERE message_id = '{message}'");

    var claim = await _claimAsync(conn, instance, commit: true);

    await Assert.That(claim.Ran(STORE_STATEMENT)).IsEqualTo(0)
      .Because("with no candidate the lock pass takes no lock, and the copy has nothing to read");
    await Assert.That(await _scalarAsync<bool>(conn,
        $"SELECT chain_emitted_at IS NOT NULL FROM wh_inbox_state WHERE message_id = '{message}'"))
      .IsTrue().Because("the stamp still runs, so the drain can fetch the row");
    await Assert.That(claim.Offered("inbox")).IsEquivalentTo([stream]);
    await Assert.That(await _scalarAsync<long>(conn, $"SELECT count(*) FROM wh_event_store WHERE event_id = '{message}'"))
      .IsEqualTo(1);
  }

  [Test]
  public async Task StorePass_AfterAPassWithNoCandidate_AReadyRowIsStoredByTheNextClaimAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var instance = Guid.NewGuid();
    await _registerAsync(conn, instance);
    var first = Guid.NewGuid();
    await _seedInboxAsync(conn, first, Guid.NewGuid(), isEvent: true, priority: 150, instance, stamped: false);
    await _claimAsync(conn, instance, commit: true);
    await _execAsync(conn, $"UPDATE wh_inbox_state SET chain_emitted_at = NULL WHERE message_id = '{first}'");
    var quiet = await _claimAsync(conn, instance, commit: true);
    await Assert.That(quiet.Ran(STORE_STATEMENT)).IsEqualTo(0).Because("precondition: the pass found no candidate");

    var ready = Guid.NewGuid();
    var readyStream = Guid.NewGuid();
    await _seedInboxAsync(conn, ready, readyStream, isEvent: true, priority: 150, instance, stamped: false);
    var claim = await _claimAsync(conn, instance, commit: true);

    await Assert.That(await _scalarAsync<long>(conn, $"SELECT count(*) FROM wh_event_store WHERE event_id = '{ready}'"))
      .IsEqualTo(1).Because("a row that is ready is stored on the next claim; the shortcut never holds work back");
    await Assert.That(claim.Offered("inbox")).Contains(readyStream);
  }

  [Test]
  public async Task StorePass_AnOrdinaryEvent_RunsNoCollectiveRoutingAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var instance = Guid.NewGuid();
    await _registerAsync(conn, instance);
    var message = Guid.NewGuid();
    await _seedInboxAsync(conn, message, Guid.NewGuid(), isEvent: true, priority: 150, instance, stamped: false);

    var claim = await _claimAsync(conn, instance, commit: true);

    await Assert.That(await _scalarAsync<long>(conn, $"SELECT count(*) FROM wh_event_store WHERE event_id = '{message}'"))
      .IsEqualTo(1).Because("precondition: the pass stored the event");
    await Assert.That(claim.Ran(COLLECTIVE_INSERT)).IsEqualTo(0)
      .Because("no stored event carries the collective flag, so the routing insert has nothing to route");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_events WHERE perspective_name = '__collective__'"))
      .IsEqualTo(0);
  }

  [Test]
  public async Task StorePass_ACollectiveEvent_IsStillRoutedToTheCollectiveSinkAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var instance = Guid.NewGuid();
    await _registerAsync(conn, instance);
    var ordinary = Guid.NewGuid();
    var collective = Guid.NewGuid();
    await _seedInboxAsync(conn, ordinary, Guid.NewGuid(), isEvent: true, priority: 150, instance, stamped: false);
    await _seedInboxAsync(conn, collective, Guid.NewGuid(), isEvent: true, priority: 150, instance, stamped: false, flags: 1);

    var claim = await _claimAsync(conn, instance, commit: true);

    await Assert.That(claim.Ran(COLLECTIVE_INSERT)).IsEqualTo(1);
    await Assert.That(await _scalarAsync<long>(conn,
        $"SELECT count(*) FROM wh_perspective_events WHERE perspective_name = '__collective__' AND event_id = '{collective}'"))
      .IsEqualTo(1).Because("a stored collective event gets its one collective sink row (061)");
    await Assert.That(await _scalarAsync<long>(conn,
        $"SELECT count(*) FROM wh_perspective_events WHERE perspective_name = '__collective__' AND event_id = '{ordinary}'"))
      .IsEqualTo(0);
  }

  // ---------------------------------------------------------------------------------------------
  // Helpers
  // ---------------------------------------------------------------------------------------------

  private static Task _registerAsync(NpgsqlConnection conn, Guid instance) =>
    _execAsync(conn, $$"""
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ('{{instance}}', 'CostSvc', 'host', 1, NOW(), NOW(), '{}'::jsonb)
      """);

  /// <summary>One pending inbox row leased to <paramref name="holder"/>; a stamped event reads as already through the store pass.</summary>
  private static Task _seedInboxAsync(
      NpgsqlConnection conn, Guid message, Guid stream, bool isEvent, int priority, Guid holder, bool stamped, int flags = 0) =>
    _execAsync(conn, $$$"""
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority, flags)
        VALUES ('{{{message}}}', 'H', '{{{EVENT_TYPE}}}', '{"p": {}}', '{}', NOW() - INTERVAL '1 minute', '{{{stream}}}', {{{isEvent}}}, {{{priority}}}, {{{flags}}})
        RETURNING message_id, stream_id, received_at, priority, is_event)
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry, chain_emitted_at)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 1, 0, '{{{holder}}}', NOW() + INTERVAL '5 minutes',
             {{{(stamped ? "NOW()" : "NULL")}}}
      FROM m
      """);

  /// <summary>
  /// A held interactive event behind an earlier pending event of its stream that nobody holds and that is not due yet,
  /// so the store pass leaves it unstored and the drain cannot fetch it.
  /// </summary>
  private static Task _seedHeldBackEventAsync(NpgsqlConnection conn, Guid instance) =>
    _execAsync(conn, $$$"""
      WITH s AS (SELECT gen_random_uuid() AS sid),
      pre AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        SELECT '00000000-0000-7000-8000-000000000001'::uuid, 'H', '{{{EVENT_TYPE}}}', '{"p": {}}', '{}', NOW() - INTERVAL '20 minutes', sid, TRUE, 250 FROM s
        RETURNING message_id, stream_id, received_at, priority, is_event),
      pre_state AS (
        INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, scheduled_for)
        SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, 0, NOW() + INTERVAL '1 day' FROM pre),
      held AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        SELECT 'ffffffff-0000-7000-8000-000000000001'::uuid, 'H', '{{{EVENT_TYPE}}}', '{"p": {}}', '{}', NOW() - INTERVAL '5 minutes', sid, TRUE, 50 FROM s
        RETURNING message_id, stream_id, received_at, priority, is_event)
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 5, 0, '{{{instance}}}', NOW() + INTERVAL '5 minutes' FROM held
      """);

  private static Task _seedPerspectiveEventAsync(NpgsqlConnection conn, Guid stream, int priority, Guid holder) =>
    _execAsync(conn, $"""
      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, priority)
      VALUES ('{stream}', 'CostPerspective', gen_random_uuid(), '{holder}', NOW() + INTERVAL '5 minutes', 0, 1, 0, {priority})
      """);

  /// <summary>One claim, with the text of every statement it ran (auto_explain, nested statements) and what it returned.</summary>
  private static async Task<ClaimRun> _claimAsync(NpgsqlConnection conn, Guid instance, bool commit, bool idleSettled = false) {
    var notices = new List<string>();
    void Collect(object? _, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    await _execAsync(conn, """
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = off;
      SET auto_explain.log_nested_statements = on;
      SET auto_explain.log_format = 'json';
      SET auto_explain.log_level = 'notice';
      """);
    conn.Notice += Collect;
    var returned = new List<(string Source, Guid? Stream)>();
    try {
      await _execAsync(conn, "BEGIN");
      await using (var cmd = conn.CreateCommand()) {
        cmd.CommandText = $"""
          SELECT source, work_stream_id FROM claim_work(
            p_instance_id => '{instance}', p_service_name => 'CostSvc', p_host_name => 'host', p_process_id => 1,
            p_max_streams => 100, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => 100,
            p_idle_settled => {(idleSettled ? "TRUE" : "FALSE")})
          """;
        cmd.CommandTimeout = 120;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
          returned.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1)));
        }
      }
      await _execAsync(conn, commit ? "COMMIT" : "ROLLBACK");
    } finally {
      conn.Notice -= Collect;
      await _execAsync(conn, "SET auto_explain.log_min_duration = -1");
    }
    var statements = new List<string>();
    foreach (var notice in notices.Where(n => n.Contains("\"Query Text\"", StringComparison.Ordinal))) {
      using var document = JsonDocument.Parse(notice[notice.IndexOf('{', StringComparison.Ordinal)..]);
      statements.Add(document.RootElement.GetProperty("Query Text").GetString() ?? string.Empty);
    }
    return new ClaimRun(statements, returned);
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 120;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private sealed record ClaimRun(IReadOnlyList<string> Statements, IReadOnlyList<(string Source, Guid? Stream)> Returned) {
    public int Rows => Returned.Count;

    /// <summary>How many statements the claim ran whose own text carries the marker.</summary>
    public int Ran(string marker) => Statements.Count(s => s.Contains(marker, StringComparison.Ordinal));

    public Guid[] Offered(string source) =>
      [.. Returned.Where(r => r.Source == source && r.Stream is not null).Select(r => r.Stream!.Value).Distinct()];
  }
}
