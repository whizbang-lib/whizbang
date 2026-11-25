// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Temporal;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A schedule occurrence's id is its outbox message id, so every fire path mints a UUIDv7 the outbox accepts (#2599):
/// the scheduled cadence, a coalesced or replayed misfire, the logged skip of a misfire and a manual trigger.
/// </summary>
/// <remarks>
/// <para>
/// The cadence id is deterministic, so two claimers racing for one occurrence agree on it and the outbox dedups the
/// second: its 48-bit timestamp is the occurrence's scheduled fire time and the rest is a hash of the schedule id and the
/// occurrence number. It used to be md5(schedule:occurrence) cast to a uuid, whose version nibble is 7 one time in
/// sixteen, so the outbox refused fifteen of every sixteen fires. A manual trigger takes a fresh v7 stamped with the
/// database clock; it used to take gen_random_uuid(), a v4 the outbox always refused.
/// </para>
/// <para>
/// The schedule ids are fixed, and chosen so that the md5 the claim used to compute is not v7 for any occurrence the
/// tests fire, so the tests fail on the old id every time rather than fifteen times in sixteen. Every test runs on a
/// database of its own, so fixed ids cannot collide.
/// </para>
/// </remarks>
/// <docs>fundamentals/temporal/temporal-engine#occurrence-ids</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/626_ScheduleOccurrenceIdsAreTimeOrdered.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class ScheduleOccurrenceIdSqlTests : EFCoreTestBase {
  private const int PARTITION_COUNT = 16;
  private const long INTERVAL_MS = 60_000;

  /// <summary>md5("{id}:{n}") for n in 0..5 has version nibble a, 9, 4, b, 5, 6: never 7.</summary>
  private static readonly Guid _scheduleA = Guid.Parse("0195a1b2-0000-7000-8000-000000002599");

  /// <summary>md5("{id}:{n}") for n in 0..5 has version nibble c, c, 2, 3, 9, f: never 7.</summary>
  private static readonly Guid _scheduleB = Guid.Parse("0195a1b2-0000-7000-8000-000000002600");

  /// <summary>A scheduled fire time with a millisecond part, so a timestamp truncated to the second would not match.</summary>
  private static readonly DateTimeOffset _fireAt = new(2026, 3, 1, 10, 0, 0, 123, TimeSpan.Zero);

  // ── the scheduled cadence ────────────────────────────────────────────────────────────────────

  [Test]
  public async Task AScheduledFire_MintsAUuidV7_StampedWithItsScheduledFireTimeAsync() {
    await using var conn = await _openAsync();
    var instance = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _pinStreamAsync(conn, stream, instance);
    await _insertScheduleAsync(conn, _scheduleA, stream, _fireAt, misfirePolicy: 0);

    var claimed = await _claimAsync(conn, instance, _fireAt.AddSeconds(5));

    await Assert.That(claimed).Count().IsEqualTo(1);
    var id = claimed[0];
    await _assertOutboxAcceptsAsync(id);
    await Assert.That(_unixMsOf(id)).IsEqualTo(_fireAt.ToUnixTimeMilliseconds())
      .Because("a cadence id's timestamp is the occurrence's scheduled fire time, not the moment a claimer got to it, " +
               "so every claimer of the occurrence computes the same id");
    await Assert.That(await _outboxMessageIdsAsync(conn, stream)).IsEquivalentTo(new[] { id })
      .Because("the occurrence id is the outbox message id");
    await Assert.That(await _runOccurrenceIdsAsync(conn, _scheduleA, status: 0)).IsEquivalentTo(new[] { id });
  }

  [Test]
  public async Task TheCadenceId_IsTheSameForTheSameOccurrence_AndDiffersAcrossOccurrencesAsync() {
    await using var conn = await _openAsync();

    var first = await _occurrenceIdAsync(conn, _scheduleA, 0, _fireAt);
    var again = await _occurrenceIdAsync(conn, _scheduleA, 0, _fireAt);
    var next = await _occurrenceIdAsync(conn, _scheduleA, 1, _fireAt.AddMilliseconds(INTERVAL_MS));
    var otherSchedule = await _occurrenceIdAsync(conn, _scheduleB, 0, _fireAt);
    var sameInstantNextOccurrence = await _occurrenceIdAsync(conn, _scheduleA, 1, _fireAt);

    await Assert.That(again).IsEqualTo(first)
      .Because("two claimers racing for one occurrence must agree on its id, or the outbox cannot dedup the second");
    await Assert.That(next).IsNotEqualTo(first);
    await Assert.That(otherSchedule).IsNotEqualTo(first)
      .Because("two schedules firing at one instant are two occurrences");
    await Assert.That(sameInstantNextOccurrence).IsNotEqualTo(first)
      .Because("the hashed bits carry the occurrence number, not only the timestamp");
  }

  [Test]
  public async Task TheCadenceId_ForEveryOccurrenceNumber_IsAUuidV7OfItsFireTimeAsync() {
    // The old md5 id was v7 one time in sixteen; 256 consecutive occurrences cannot all pass by chance.
    await using var conn = await _openAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT n, wh_schedule_occurrence_id(@s, n, @t + make_interval(secs => n * 60))
      FROM generate_series(0::bigint, 255::bigint) AS n
      ORDER BY n
      """;
    cmd.Parameters.AddWithValue("s", _scheduleB);
    cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.TimestampTz) { Value = _fireAt });
    var ids = new List<(long N, Guid Id)>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        ids.Add((reader.GetInt64(0), reader.GetGuid(1)));
      }
    }

    await Assert.That(ids).Count().IsEqualTo(256);
    foreach (var (n, id) in ids) {
      await _assertOutboxAcceptsAsync(id);
      await Assert.That(_unixMsOf(id)).IsEqualTo(_fireAt.AddMinutes(n).ToUnixTimeMilliseconds());
    }
    await Assert.That(ids.Select(i => i.Id).Distinct().Count()).IsEqualTo(256);
  }

  [Test]
  public async Task TheClaim_MintsTheCadenceIdThroughTheOneOccurrenceIdFunctionAsync() {
    await using var conn = await _openAsync();
    var instance = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _pinStreamAsync(conn, stream, instance);
    await _insertScheduleAsync(conn, _scheduleA, stream, _fireAt, misfirePolicy: 0, occurrenceCount: 3);

    var claimed = await _claimAsync(conn, instance, _fireAt.AddSeconds(5));

    await Assert.That(claimed).IsEquivalentTo(new[] { await _occurrenceIdAsync(conn, _scheduleA, 3, _fireAt) })
      .Because("every fire path takes its cadence id from wh_schedule_occurrence_id, so they cannot drift apart");
  }

  // ── misfires ─────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task ACoalescedMisfire_MintsAUuidV7_StampedWithTheMissedFireTimeAsync() {
    await using var conn = await _openAsync();
    var instance = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _pinStreamAsync(conn, stream, instance);
    await _insertScheduleAsync(conn, _scheduleA, stream, _fireAt, misfirePolicy: 0);

    // Ten intervals overdue: more than one fire was missed, so Coalesce fires once for the window.
    var claimed = await _claimAsync(conn, instance, _fireAt.AddMinutes(10).AddSeconds(5));

    await Assert.That(claimed).Count().IsEqualTo(1);
    await _assertOutboxAcceptsAsync(claimed[0]);
    await Assert.That(_unixMsOf(claimed[0])).IsEqualTo(_fireAt.ToUnixTimeMilliseconds());
    await Assert.That(await _outboxMessageIdsAsync(conn, stream)).IsEquivalentTo(claimed);
  }

  [Test]
  public async Task ACatchUpReplay_MintsAUuidV7PerMissedOccurrence_EachStampedWithItsOwnFireTimeAsync() {
    await using var conn = await _openAsync();
    var instance = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _pinStreamAsync(conn, stream, instance);
    await _insertScheduleAsync(conn, _scheduleA, stream, _fireAt, misfirePolicy: 1);
    var now = _fireAt.AddMinutes(3).AddSeconds(5);

    var first = await _claimAsync(conn, instance, now);
    var second = await _claimAsync(conn, instance, now);

    await Assert.That(first).Count().IsEqualTo(1);
    await Assert.That(second).Count().IsEqualTo(1);
    await _assertOutboxAcceptsAsync(first[0]);
    await _assertOutboxAcceptsAsync(second[0]);
    await Assert.That(_unixMsOf(first[0])).IsEqualTo(_fireAt.ToUnixTimeMilliseconds());
    await Assert.That(_unixMsOf(second[0])).IsEqualTo(_fireAt.AddMilliseconds(INTERVAL_MS).ToUnixTimeMilliseconds())
      .Because("CatchUp replays each missed occurrence, and each one's id carries its own scheduled time");
    await Assert.That(await _outboxMessageIdsAsync(conn, stream)).IsEquivalentTo(new[] { first[0], second[0] });
  }

  [Test]
  public async Task ACatchUpReplayPastItsLookback_MintsAUuidV7Async() {
    await using var conn = await _openAsync();
    var instance = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _pinStreamAsync(conn, stream, instance);
    await _insertScheduleAsync(conn, _scheduleA, stream, _fireAt, misfirePolicy: 1, catchUpLookbackMs: 2 * INTERVAL_MS);

    var claimed = await _claimAsync(conn, instance, _fireAt.AddMinutes(30).AddSeconds(5));

    await Assert.That(claimed).Count().IsEqualTo(1);
    await _assertOutboxAcceptsAsync(claimed[0]);
    await Assert.That(await _outboxMessageIdsAsync(conn, stream)).IsEquivalentTo(claimed);
  }

  [Test]
  public async Task ASkippedMisfire_LogsItsRunUnderAUuidV7Async() {
    await using var conn = await _openAsync();
    var instance = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _pinStreamAsync(conn, stream, instance);
    await _insertScheduleAsync(conn, _scheduleA, stream, _fireAt, misfirePolicy: 2);

    var claimed = await _claimAsync(conn, instance, _fireAt.AddMinutes(10).AddSeconds(5));

    await Assert.That(claimed).Count().IsEqualTo(1);
    await _assertOutboxAcceptsAsync(claimed[0]);
    await Assert.That(await _runOccurrenceIdsAsync(conn, _scheduleA, status: 2)).IsEquivalentTo(claimed)
      .Because("the skip is logged under the id the occurrence would have had, and that id is a v7 like every other");
    await Assert.That(await _outboxMessageIdsAsync(conn, stream)).IsEmpty()
      .Because("a skipped misfire emits nothing");
  }

  // ── a manual trigger ─────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task ATriggerNow_MintsAFreshUuidV7_StampedWithTheDatabaseClockAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _insertScheduleAsync(conn, _scheduleA, stream, _fireAt.AddYears(10), misfirePolicy: 0);

    var first = await _triggerAsync(conn, _scheduleA);
    var second = await _triggerAsync(conn, _scheduleA);

    await _assertOutboxAcceptsAsync(first);
    await _assertOutboxAcceptsAsync(second);
    await Assert.That(second).IsNotEqualTo(first).Because("each manual trigger is an occurrence of its own");
    var firedAt = await _runFiredAtAsync(conn, first);
    await Assert.That(_unixMsOf(first)).IsEqualTo(firedAt.ToUnixTimeMilliseconds())
      .Because("a manual trigger's id is stamped with the moment it fired on the database clock");
    await Assert.That(first).IsNotEqualTo(await _occurrenceIdAsync(conn, _scheduleA, 0, _fireAt.AddYears(10)))
      .Because("a manual trigger never takes the id of the cadence occurrence still to come");
    await Assert.That(await _outboxMessageIdsAsync(conn, stream)).IsEquivalentTo(new[] { first, second });
  }

  // ── stored ids ───────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task ARunLoggedUnderTheOldId_KeepsItsId_WhenTheNextOccurrenceFiresAsync() {
    await using var conn = await _openAsync();
    var instance = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _pinStreamAsync(conn, stream, instance);
    await _insertScheduleAsync(conn, _scheduleA, stream, _fireAt, misfirePolicy: 0, occurrenceCount: 1);
    Guid legacy;
    await using (var seed = conn.CreateCommand()) {
      seed.CommandText = """
        INSERT INTO wh_schedule_runs (schedule_id, occurrence_id, fired_at, status, instance_id)
        VALUES (@s, md5(@s::text || ':0')::uuid, @t, 0, @i)
        RETURNING occurrence_id
        """;
      seed.Parameters.AddWithValue("s", _scheduleA);
      seed.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.TimestampTz) { Value = _fireAt.AddMinutes(-1) });
      seed.Parameters.AddWithValue("i", instance);
      legacy = (Guid)(await seed.ExecuteScalarAsync())!;
    }

    var claimed = await _claimAsync(conn, instance, _fireAt.AddSeconds(5));

    await Assert.That(await _runOccurrenceIdsAsync(conn, _scheduleA, status: 0)).IsEquivalentTo(new[] { legacy, claimed[0] })
      .Because("ids already stored are history and are never rewritten; only new occurrences take the v7 id");
    await Assert.That(claimed[0]).IsNotEqualTo(legacy);
  }

  // ── published through the outbox ─────────────────────────────────────────────────────────────

  [Test]
  public async Task AScheduledFire_IsPublishedThroughTheOutboxUnderItsOccurrenceIdAsync() {
    var (provider, jsonOptions) = _createService();
    await using var _ = provider;
    var instance = provider.GetRequiredService<IServiceInstanceProvider>();
    var stream = (Guid)TrackedGuid.New();
    Guid id;
    await using (var conn = await _openAsync()) {
      await _pinStreamAsync(conn, stream, instance.InstanceId);
      await _insertScheduleAsync(conn, _scheduleA, stream, _fireAt, misfirePolicy: 0);
      var claimed = await _claimAsync(conn, instance.InstanceId, _fireAt.AddSeconds(5));
      await Assert.That(claimed).Count().IsEqualTo(1);
      id = claimed[0];
    }

    await OutboxDrainHarness.LeaseAsync(ConnectionString, instance, id);
    var published = await OutboxDrainHarness.DrainAndCaptureAsync(provider, jsonOptions, bulkPublish: false, id, stream);

    await Assert.That(published).Count().IsEqualTo(1)
      .Because("the drain reads the occurrence's envelope, whose message id must be a v7, and hands it to the transport");
    await Assert.That(published[0].MessageId).IsEqualTo(id);
    await Assert.That(published[0].Envelope.MessageId.Value).IsEqualTo(id);
  }

  [Test]
  public async Task ATriggerNowAsync_IsPublishedThroughTheOutboxUnderItsOccurrenceIdAsync() {
    var (provider, jsonOptions) = _createService();
    await using var _ = provider;
    var instance = provider.GetRequiredService<IServiceInstanceProvider>();
    var stream = (Guid)TrackedGuid.New();
    await using (var conn = await _openAsync()) {
      await _insertScheduleAsync(conn, _scheduleB, stream, _fireAt.AddYears(10), misfirePolicy: 0);
    }
    var manager = new PgScheduleManager(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      instance,
      Options.Create(new ClaimWorkerOptions { PartitionCount = PARTITION_COUNT }),
      Options.Create(new TemporalOptions()),
      NullLogger<PgScheduleManager>.Instance);

    var triggered = await manager.TriggerNowAsync(_scheduleB);

    await Assert.That(triggered.HasValue).IsTrue();
    var id = triggered!.Value;
    await _assertOutboxAcceptsAsync(id);
    await OutboxDrainHarness.LeaseAsync(ConnectionString, instance, id);
    var published = await OutboxDrainHarness.DrainAndCaptureAsync(provider, jsonOptions, bulkPublish: false, id, stream);

    await Assert.That(published).Count().IsEqualTo(1)
      .Because("a manual trigger used to mint a v4 id, which the drain refused every time");
    await Assert.That(published[0].MessageId).IsEqualTo(id);
    await Assert.That(published[0].Envelope.MessageId.Value).IsEqualTo(id);
  }

  // ── helpers ──────────────────────────────────────────────────────────────────────────────────

  /// <summary>The check the outbox makes when it reads an occurrence's envelope: a message id is a UUIDv7.</summary>
  private static async Task _assertOutboxAcceptsAsync(Guid id) {
    await Assert.That(() => MessageId.From(id)).ThrowsNothing()
      .Because($"the outbox refuses a message id that is not a UUIDv7, and {id} is version {id.Version}");
    await Assert.That("89ab".Contains(id.ToString("N")[16], StringComparison.Ordinal)).IsTrue()
      .Because($"a UUIDv7 carries the RFC 4122 variant, and {id} does not");
  }

  /// <summary>The 48-bit unix-millisecond timestamp at the front of a UUIDv7.</summary>
  private static long _unixMsOf(Guid id) =>
    long.Parse(id.ToString("N")[..12], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _pinStreamAsync(NpgsqlConnection conn, Guid streamId, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      VALUES (@s, 0, @i, NOW(), NOW())
      ON CONFLICT (stream_id) DO UPDATE SET assigned_instance_id = EXCLUDED.assigned_instance_id
      """;
    cmd.Parameters.AddWithValue("s", streamId);
    cmd.Parameters.AddWithValue("i", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertScheduleAsync(
      NpgsqlConnection conn, Guid scheduleId, Guid streamId, DateTimeOffset nextFireAt, short misfirePolicy,
      long occurrenceCount = 0, long? catchUpLookbackMs = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_schedules
        (schedule_id, stream_id, partition_number, recurrence_kind, interval_ms, timezone,
         next_fire_at, occurrence_count, status, event_type, event_data, misfire_policy, catch_up_lookback_ms)
      VALUES (@id, @stream, 0, 1, @interval, 'UTC', @next, @occ, 0, 'Whizbang.Tests.ScheduleOccurrence', '{}'::jsonb,
              @misfire, @lookback)
      """;
    cmd.Parameters.AddWithValue("id", scheduleId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("interval", INTERVAL_MS);
    cmd.Parameters.Add(new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = nextFireAt });
    cmd.Parameters.AddWithValue("occ", occurrenceCount);
    cmd.Parameters.Add(new NpgsqlParameter("misfire", NpgsqlDbType.Smallint) { Value = misfirePolicy });
    cmd.Parameters.AddWithValue("lookback", (object?)catchUpLookbackMs ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>One claim at a fixed instant (the function's test seam), returning the occurrence ids it minted.</summary>
  private static async Task<List<Guid>> _claimAsync(NpgsqlConnection conn, Guid instanceId, DateTimeOffset now) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT o_occurrence_id FROM wh_claim_due_schedules(
        p_instance_id => @i, p_lease_expiry => @lease, p_partition_count => @pc, p_limit => 100, p_now => @now)
      """;
    cmd.Parameters.AddWithValue("i", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(now), NpgsqlDbType.TimestampTz) { Value = now });
    cmd.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) { Value = now.AddMinutes(5) });
    cmd.Parameters.Add(new NpgsqlParameter("pc", NpgsqlDbType.Integer) { Value = PARTITION_COUNT });
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<Guid> _triggerAsync(NpgsqlConnection conn, Guid scheduleId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT o_occurrence_id FROM wh_trigger_schedule_now(@id, @i, NOW() + INTERVAL '5 minutes', @pc)
      """;
    cmd.Parameters.AddWithValue("id", scheduleId);
    cmd.Parameters.AddWithValue("i", (Guid)TrackedGuid.New());
    cmd.Parameters.Add(new NpgsqlParameter("pc", NpgsqlDbType.Integer) { Value = PARTITION_COUNT });
    return (Guid)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<Guid> _occurrenceIdAsync(NpgsqlConnection conn, Guid scheduleId, long occurrence, DateTimeOffset fireAt) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_schedule_occurrence_id(@s, @n, @t)";
    cmd.Parameters.AddWithValue("s", scheduleId);
    cmd.Parameters.AddWithValue("n", occurrence);
    cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.TimestampTz) { Value = fireAt });
    return (Guid)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<List<Guid>> _outboxMessageIdsAsync(NpgsqlConnection conn, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT message_id FROM wh_outbox WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", streamId);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<List<Guid>> _runOccurrenceIdsAsync(NpgsqlConnection conn, Guid scheduleId, short status) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT occurrence_id FROM wh_schedule_runs WHERE schedule_id = @s AND status = @st";
    cmd.Parameters.AddWithValue("s", scheduleId);
    cmd.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Smallint) { Value = status });
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<DateTimeOffset> _runFiredAtAsync(NpgsqlConnection conn, Guid occurrenceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT fired_at FROM wh_schedule_runs WHERE occurrence_id = @o";
    cmd.Parameters.AddWithValue("o", occurrenceId);
    return (DateTimeOffset)(await cmd.ExecuteScalarAsync())!;
  }

  private (ServiceProvider Provider, System.Text.Json.JsonSerializerOptions JsonOptions) _createService() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider((Guid)TrackedGuid.New(), "occ-svc", "occ-host", 1));
    services.AddScoped(_ => CreateDbContext());

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

    services.AddScoped<IWorkCoordinator>(sp =>
      new EFCoreWorkCoordinator<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangLifecycleMessageDeserializer();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    return (services.BuildServiceProvider(), jsonOptions);
  }
}
