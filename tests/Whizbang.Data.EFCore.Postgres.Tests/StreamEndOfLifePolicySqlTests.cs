// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The end-of-life policy triggers (#1740, WS-3 S11, migration 297), against a database the EF Core driver migrated: each
/// trigger picks a stream just outside its threshold and leaves one just inside it alone, read from the lifecycle ledger
/// alone; the version cap leaves exactly its tail hot; the tenant byte cap picks the least recently appended streams first;
/// the seal that guards a purge holds only for an unchanged stream; and a due stream carries its type, whose action its
/// disposition runs.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#policies</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresStreamDispositionLedger.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/297_StreamEndOfLifePolicies.sql</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class StreamEndOfLifePolicySqlTests : EFCoreTestBase {
  private const string ORDER = "Whizbang.Tests.PolicyOrder";
  private const string OTHER = "Whizbang.Tests.PolicyOther";
  private static readonly DateTimeOffset _t0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

  private PostgresStreamDispositionLedger _ledger() =>
    new(new PostgresArchiveConnection(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, Schema: null));

  private static StreamEndOfLifePolicy _policy(string type = ORDER, TimeSpan? idle = null, TimeSpan? age = null, long? keep = null, long? cap = null) =>
    new(type, idle, age, keep, cap, StreamLifecycleAction.Archive);

  // ===== The idle trigger =====

  [Test]
  public async Task IdleTtl_PicksAStreamOnceItsNewestAppendsHourIsAFullTtlOld_AndNotASecondBeforeAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = await _streamAsync(conn);
    // The newest append was some time in the 09:00 hour: the stream is certainly idle only from 10:00 on.
    await _executeAsync(conn, "UPDATE wh_stream_lifecycle SET last_appended_bucket = @b WHERE stream_id = @s", ("b", _t0), ("s", stream));
    var ttl = TimeSpan.FromDays(30);
    var boundary = _t0.AddHours(1) + ttl;

    var inside = await _ledger().GetPolicyCandidatesAsync([_policy(idle: ttl)], boundary.AddSeconds(-1), 10);
    var outside = await _ledger().GetPolicyCandidatesAsync([_policy(idle: ttl)], boundary, 10);

    await Assert.That(inside).IsEmpty()
      .Because("the bucket is the hour of the newest append, so the stream may have been written up to the end of that hour");
    await Assert.That(outside).IsEquivalentTo([new StreamPolicyCandidate(stream, ORDER, StreamEndOfLifeTrigger.IdleTtl, 1, null)]);
  }

  // ===== The age trigger =====

  [Test]
  public async Task MaxAge_PicksAStreamWhoseFirstAppendIsAMaxAgeOld_AndNotAMicrosecondBeforeAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = await _streamAsync(conn);
    await _executeAsync(conn, "UPDATE wh_stream_lifecycle SET first_appended_at = @f WHERE stream_id = @s", ("f", _t0), ("s", stream));
    var age = TimeSpan.FromDays(365);

    var inside = await _ledger().GetPolicyCandidatesAsync([_policy(age: age)], _t0 + age - TimeSpan.FromTicks(10), 10);
    var outside = await _ledger().GetPolicyCandidatesAsync([_policy(age: age)], _t0 + age, 10);

    await Assert.That(inside).IsEmpty();
    await Assert.That(outside).IsEquivalentTo([new StreamPolicyCandidate(stream, ORDER, StreamEndOfLifeTrigger.MaxAge, 1, null)])
      .Because("the first append's time is exact, so the age boundary is exact too");
  }

  // ===== The version cap =====

  [Test]
  public async Task KeepTailVersions_PicksAStreamWithMoreHotVersions_ThroughTheVersionThatLeavesExactlyTheTail_AndTheTruncateLeavesItAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = await _streamAsync(conn, events: 5);

    var atTheCap = await _ledger().GetPolicyCandidatesAsync([_policy(keep: 5)], _t0, 10);
    var overTheCap = await _ledger().GetPolicyCandidatesAsync([_policy(keep: 2)], _t0, 10);
    await _executeAsync(conn, "SELECT * FROM wh_truncate_stream_prefix(@s, @v, FALSE)", ("s", stream), ("v", overTheCap[0].ThroughVersion!.Value));
    await _ledger().SetTruncatedThroughAsync(stream, overTheCap[0].ThroughVersion!.Value);
    var afterTheCut = await _ledger().GetPolicyCandidatesAsync([_policy(keep: 2)], _t0, 10);

    await Assert.That(atTheCap).IsEmpty().Because("five hot versions with a tail of five is within the cap");
    await Assert.That(overTheCap).IsEquivalentTo([new StreamPolicyCandidate(stream, ORDER, StreamEndOfLifeTrigger.VersionCap, 5, 3)]);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_store WHERE stream_id = @s", ("s", stream))).IsEqualTo(2L)
      .Because("the cut through the returned version leaves exactly the tail hot");
    await Assert.That(afterTheCut).IsEmpty().Because("the recorded cut counts the truncated prefix as gone, so the stream is within the cap again");
  }

  [Test]
  public async Task SetTruncatedThrough_RecordsTheCut_AndNeverMovesItBackAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = await _streamAsync(conn, events: 3);

    await _ledger().SetTruncatedThroughAsync(stream, 2);
    await _ledger().SetTruncatedThroughAsync(stream, 1);

    await Assert.That(await _scalarAsync<long>(conn, "SELECT truncated_through_version FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", stream)))
      .IsEqualTo(2L).Because("a prefix that left the hot store cannot come back by recording a shorter cut");
  }

  // ===== The tenant byte cap =====

  [Test]
  public async Task TenantByteCap_PicksTheLeastRecentlyAppendedStreamsUntilTheRestFit_PerTenant_WithNoTenantAsOneGroupAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var oldest = await _ledgerRowAsync(conn, "tenant-a", 400, _t0.AddHours(-3));
    var middle = await _ledgerRowAsync(conn, "tenant-a", 300, _t0.AddHours(-2));
    var newest = await _ledgerRowAsync(conn, "tenant-a", 300, _t0.AddHours(-1));
    var fitsB = await _ledgerRowAsync(conn, "tenant-b", 1000, _t0.AddHours(-9));
    var noTenantOld = await _ledgerRowAsync(conn, null, 700, _t0.AddHours(-5));
    var noTenantNew = await _ledgerRowAsync(conn, null, 400, _t0.AddHours(-4));

    var atTheCap = await _ledgerAsync([_policy(cap: 1000)]);
    var underTheCapByOne = await _ledgerAsync([_policy(cap: 999)]);
    var tight = await _ledgerAsync([_policy(cap: 500)]);

    await Assert.That(atTheCap.Select(c => c.StreamId)).IsEquivalentTo([noTenantOld])
      .Because("tenant a holds exactly 1,000 bytes, which fits; the streams with no tenant hold 1,100 together, so the oldest goes");
    await Assert.That(underTheCapByOne.Select(c => c.StreamId)).IsEquivalentTo([fitsB, noTenantOld, oldest], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a byte under each total, each tenant sheds its least recently appended stream, and the picks come oldest first");
    await Assert.That(tight.Select(c => c.StreamId)).IsEquivalentTo([fitsB, noTenantOld, oldest, middle], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the newest streams are kept while they fit, and a single stream larger than the cap does not fit either");
    await Assert.That(tight.All(c => c.Trigger == StreamEndOfLifeTrigger.TenantByteCap)).IsTrue();
    await Assert.That(tight.Select(c => c.StreamId)).DoesNotContain(newest);
    await Assert.That(tight.Select(c => c.StreamId)).DoesNotContain(noTenantNew);
  }

  // ===== What a trigger never picks, the order and the limit =====

  [Test]
  public async Task Triggers_PickOnlyOpenUnscheduledStreamsOfAPolicysType_LeastRecentlyAppendedFirst_TheFirstTriggerWinning_UpToTheLimitAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var idleAndCapped = await _streamAsync(conn, events: 3);
    var later = await _streamAsync(conn);
    var sealedStream = await _streamAsync(conn);
    await _executeAsync(conn, "SELECT * FROM wh_finalize_stream(@s, 'ended', @at)", ("s", sealedStream), ("at", _t0));
    var scheduled = await _streamAsync(conn);
    await _ledger().SetDueAsync(scheduled, _t0.AddDays(1));
    var otherType = await _streamAsync(conn, type: OTHER);
    var archived = await _streamAsync(conn);
    await Assert.That((await new PostgresTableArchiveStore(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync)
      .WriteAsync(ArchiveBatch.WholeStream(archived))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await _executeAsync(conn, "UPDATE wh_stream_lifecycle SET last_appended_bucket = @b", ("b", _t0.AddDays(-60)));
    await _executeAsync(conn, "UPDATE wh_stream_lifecycle SET last_appended_bucket = @b WHERE stream_id = @s", ("b", _t0.AddDays(-50)), ("s", later));
    var policies = new[] { _policy(idle: TimeSpan.FromDays(30), keep: 1) };

    var all = await _ledger().GetPolicyCandidatesAsync(policies, _t0, 10);
    var limited = await _ledger().GetPolicyCandidatesAsync(policies, _t0, 1);

    await Assert.That(all).IsEquivalentTo([
      new StreamPolicyCandidate(idleAndCapped, ORDER, StreamEndOfLifeTrigger.IdleTtl, 3, null),
      new StreamPolicyCandidate(later, ORDER, StreamEndOfLifeTrigger.IdleTtl, 1, null),
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a sealed stream is its seal's to dispose of, a scheduled one is waiting out a deferral, an archived one is gone, "
        + "and a whole-stream trigger wins over the version cap, which it subsumes");
    await Assert.That(limited.Select(c => c.StreamId)).IsEquivalentTo([idleAndCapped]);
    await Assert.That(all.Select(c => c.StreamId)).DoesNotContain(otherType);
  }

  [Test]
  public async Task PolicyCandidates_ReadOnlyTheLifecycleLedger_AndNoOtherTableAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    for (var i = 0; i < 5; i++) {
      _ = await _streamAsync(conn, events: 2);
    }
    await using var tx = await conn.BeginTransactionAsync();
    var before = await _tableReadsAsync(conn);

    await using (var c = conn.CreateCommand()) {
      c.CommandText = "SELECT count(*) FROM wh_stream_policy_candidates(@now, 100, ARRAY[@t]::text[], ARRAY[1]::bigint[], ARRAY[1]::bigint[], ARRAY[1]::bigint[], ARRAY[1]::bigint[])";
      c.Parameters.AddWithValue("now", _t0.AddDays(400));
      c.Parameters.AddWithValue("t", ORDER);
      await Assert.That((long)(await c.ExecuteScalarAsync())!).IsEqualTo(5L).Because("the control: the triggers did pick the streams");
    }
    var after = await _tableReadsAsync(conn);
    await tx.RollbackAsync();

    var read = after.Where(a => !before.TryGetValue(a.Key, out var was) || was != a.Value).Select(a => a.Key).ToList();
    await Assert.That(read).IsEquivalentTo(["wh_stream_lifecycle"])
      .Because("finding end-of-life candidates must not cost a read of the event store, the cost the ledger exists to remove");
  }

  // ===== Readiness, the guarding seal, and the due stream's type =====

  [Test]
  public async Task GetReadiness_ReadsTheConsumptionGateAndTheHolds_PerStreamAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var ready = await _streamAsync(conn);
    await _executeAsync(conn,
      "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
      "VALUES (@id, @t, 'perspective', 'Probe.P', 'test-service', @t, NOW(), NOW())", ("id", Guid.CreateVersion7()), ("t", OTHER));
    var blocked = await _streamAsync(conn, type: OTHER);
    var held = await _streamAsync(conn);
    await _executeAsync(conn,
      "INSERT INTO wh_event_destruction_hold (event_id, hold_until) SELECT es.event_id, NOW() + INTERVAL '1 day' FROM wh_event_store es WHERE es.stream_id = @s",
      ("s", held));

    var readiness = await _ledger().GetReadinessAsync([ready, blocked, held]);

    await Assert.That(readiness[ready]).IsEqualTo(StreamDispositionReadiness.Ready);
    await Assert.That(readiness[blocked]).IsEqualTo(StreamDispositionReadiness.Blocked);
    await Assert.That(readiness[held]).IsEqualTo(StreamDispositionReadiness.Held);
  }

  [Test]
  public async Task SealIfUnchanged_SealsAnOpenStreamAtTheVersionSeen_AndNotOneAppendedSinceOrAlreadySealedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var unchanged = await _streamAsync(conn, events: 2);
    var appended = await _streamAsync(conn, events: 3);
    var alreadySealed = await _streamAsync(conn);
    await _executeAsync(conn, "SELECT * FROM wh_finalize_stream(@s, 'ended', @at)", ("s", alreadySealed), ("at", _t0));

    var sealedIt = await _ledger().SealIfUnchangedAsync(unchanged, 2, "End-of-life policy", _t0.AddHours(1));
    var changed = await _ledger().SealIfUnchangedAsync(appended, 2, "End-of-life policy", _t0.AddHours(1));
    var twice = await _ledger().SealIfUnchangedAsync(alreadySealed, 1, "End-of-life policy", _t0.AddHours(1));

    await Assert.That(sealedIt).IsTrue();
    await Assert.That(await _scalarAsync<string>(conn, "SELECT state || '|' || sealed_reason FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", unchanged)))
      .IsEqualTo("Sealed|End-of-life policy");
    await Assert.That(changed).IsFalse().Because("the stream was appended to after the policy saw version 2, so it is alive again");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT state FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", appended))).IsEqualTo("Active");
    await Assert.That(twice).IsFalse().Because("an existing seal keeps its own time and reason");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT sealed_reason FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", alreadySealed))).IsEqualTo("ended");
  }

  [Test]
  public async Task SealIfUnchanged_WaitsForTheStreamsAppendLockAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = await _streamAsync(conn);
    await using var appender = new NpgsqlConnection(ConnectionString);
    await appender.OpenAsync();
    await using var appending = await appender.BeginTransactionAsync();
    await _executeAsync(appender, "SELECT pg_advisory_xact_lock(hashtext('wh_event_store:' || @s::text))", ("s", stream));

    await using (var held = await conn.BeginTransactionAsync()) {
      await _executeAsync(conn, "SET LOCAL lock_timeout = '100ms'");
      var waited = await Assert.That(async () => await _executeAsync(conn,
          "SELECT wh_seal_stream_if_unchanged(@s, 1, 'policy', @at)", ("s", stream), ("at", _t0)))
        .Throws<PostgresException>();
      await Assert.That(waited!.SqlState).IsEqualTo(PostgresErrorCodes.LockNotAvailable)
        .Because("the guard compares the version under the lock every append takes, so no append slips between the check and the seal");
      await held.RollbackAsync();
    }

    await appending.CommitAsync();
    await Assert.That(await _ledger().SealIfUnchangedAsync(stream, 1, "policy", _t0)).IsTrue().Because("the control: free, the same call seals");
  }

  [Test]
  public async Task GetDue_CarriesEachStreamsType_SoItsDispositionIsTheTypesActionAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = await _streamAsync(conn, type: OTHER);
    await _executeAsync(conn, "SELECT * FROM wh_finalize_stream(@s, 'ended', @at)", ("s", stream), ("at", _t0));
    await _ledger().SetDueAsync(stream, _t0);

    var due = await _ledger().GetDueAsync(_t0, 10);

    await Assert.That(due).IsEquivalentTo([new DueStreamDisposition(stream, StreamDispositionReadiness.Ready, OTHER)]);
  }

  // ===== End to end =====

  [Test]
  public async Task Step_WithAnIdlePolicy_ArchivesAStreamOnceItIsIdle_AndNotBefore_AndOnlyWhenEnabledAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = await _streamAsync(conn);
    await _executeAsync(conn, "UPDATE wh_stream_lifecycle SET last_appended_bucket = @b WHERE stream_id = @s", ("b", _t0), ("s", stream));
    var ttl = TimeSpan.FromDays(10);
    var lifecycle = new StreamLifecycleOptions { Enabled = true };
    lifecycle.Types[ORDER] = new StreamTypeLifecycleOptions { IdleTtl = ttl };
    var disabled = new StreamLifecycleOptions();
    disabled.Types[ORDER] = new StreamTypeLifecycleOptions { IdleTtl = ttl };
    var clock = new FakeTimeProvider(_t0.AddHours(1) + ttl - TimeSpan.FromSeconds(1));
    var step = new StreamDispositionStep(clock, NullLogger<StreamDispositionStep>.Instance);

    await step.RunAsync(_services(lifecycle), CancellationToken.None);
    var stateJustBefore = await _scalarAsync<string>(conn, "SELECT state FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", stream));
    clock.Advance(TimeSpan.FromSeconds(1));
    await step.RunAsync(_services(disabled), CancellationToken.None);
    var stateWhenDisabled = await _scalarAsync<string>(conn, "SELECT state FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", stream));
    await step.RunAsync(_services(lifecycle), CancellationToken.None);

    await Assert.That(stateJustBefore).IsEqualTo("Active");
    await Assert.That(stateWhenDisabled).IsEqualTo("Active").Because("a policy acts only once Enabled is true");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT state FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", stream))).IsEqualTo("Archived")
      .Because("the default action archives the whole stream, which stays readable");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_store WHERE stream_id = @s", ("s", stream))).IsEqualTo(0L);
  }

  // =================================================================================================

  private IServiceProvider _services(StreamLifecycleOptions lifecycle) =>
    new ServiceCollection()
      .AddSingleton<IStreamDispositionLedger>(_ledger())
      .AddSingleton<IArchiveStore>(new PostgresTableArchiveStore(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync))
      .AddSingleton(Options.Create(lifecycle))
      .AddSingleton(Options.Create(new MaintenanceWorkerOptions()))
      .BuildServiceProvider();

  private Task<IReadOnlyList<StreamPolicyCandidate>> _ledgerAsync(IReadOnlyList<StreamEndOfLifePolicy> policies) =>
    _ledger().GetPolicyCandidatesAsync(policies, _t0, 100);

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  // A stream of `events` Sourced events through the emit chain, versions 1 to `events`.
  private static async Task<Guid> _streamAsync(NpgsqlConnection connection, string type = ORDER, int events = 1) {
    var stream = Guid.CreateVersion7();
    var messages = string.Join(",", Enumerable.Range(0, events).Select(_ => {
      var eventId = Guid.CreateVersion7();
      return $$"""
        {
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{type}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": null, "Scope": null, "StreamId": "{{stream}}", "IsEvent": true, "Flags": 0
        }
        """;
    }));
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{{messages}}]
      }
      """;
    await _executeAsync(connection, "SELECT commit_handler_result(@req::jsonb)", ("req", request));
    return stream;
  }

  // A ledger row of ORDER with the given tenant, size and newest-append hour; the tenant cap reads nothing else.
  private static async Task<Guid> _ledgerRowAsync(NpgsqlConnection connection, string? tenant, long bytes, DateTimeOffset bucket) {
    var stream = await _streamAsync(connection);
    await using var c = connection.CreateCommand();
    c.CommandText = "UPDATE wh_stream_lifecycle SET tenant = @t, approx_bytes = @b, last_appended_bucket = @at WHERE stream_id = @s";
    c.Parameters.Add(new NpgsqlParameter("t", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)tenant ?? DBNull.Value });
    c.Parameters.AddWithValue("b", bytes);
    c.Parameters.AddWithValue("at", bucket);
    c.Parameters.AddWithValue("s", stream);
    _ = await c.ExecuteNonQueryAsync();
    return stream;
  }

  // The scans of every table this transaction has made so far, by table.
  private static async Task<Dictionary<string, long>> _tableReadsAsync(NpgsqlConnection connection) {
    await using var c = connection.CreateCommand();
    c.CommandText = "SELECT relname, COALESCE(seq_scan, 0) + COALESCE(idx_scan, 0) FROM pg_stat_xact_user_tables WHERE schemaname = current_schema()";
    var reads = new Dictionary<string, long>(StringComparer.Ordinal);
    await using var reader = await c.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      reads[reader.GetString(0)] = reader.GetInt64(1);
    }
    return reads;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) {
    await using var c = connection.CreateCommand();
    c.CommandText = sql;
    foreach (var (name, value) in parameters) {
      c.Parameters.AddWithValue(name, value);
    }
    return (T)(await c.ExecuteScalarAsync())!;
  }

  private static async Task _executeAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) {
    await using var c = connection.CreateCommand();
    c.CommandText = sql;
    foreach (var (name, value) in parameters) {
      c.Parameters.AddWithValue(name, value);
    }
    _ = await c.ExecuteNonQueryAsync();
  }
}
