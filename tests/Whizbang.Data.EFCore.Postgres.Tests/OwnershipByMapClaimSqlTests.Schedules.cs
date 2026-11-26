// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Temporal;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2575 in map mode. A schedule fires on the owner of its partition, so a stream with no ledger row (the shape that
/// never fired in the legacy mode) is already its partition owner's. The precise wake and the due poll must answer by
/// the same rule as the claim: they joined the stored owner, which map mode never pins, so they never rang for it.
/// The legacy mode's "no live owner" rule must not leak into map mode: a non-owner never fires it.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine#which-instance-fires-a-schedule</docs>
public partial class OwnershipByMapClaimSqlTests {
  [Test]
  public async Task InMapMode_AScheduleWithNoLedgerRow_FiresOnItsPartitionsOwnerOnlyAsync() {
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _publishAsync(conn, epoch, [_a, _b]);
    await _dueScheduleAsync(conn, (Guid)TrackedGuid.New(), MOVED);

    var byA = await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_claim_due_schedules(@inst, NOW() + INTERVAL '5 minutes', 10000)", ("inst", _a));
    var byB = await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_claim_due_schedules(@inst, NOW() + INTERVAL '5 minutes', 10000)", ("inst", _b));

    await Assert.That(byA).IsEqualTo(0L)
      .Because("in map mode the partition's owner decides; a stream no one holds is not every instance's there");
    await Assert.That(byB).IsEqualTo(1L);
  }

  [Test]
  public async Task InMapMode_TheNextFireTime_IsTheOwnedPartitionsSchedule_WithNoLedgerRowAsync() {
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _publishAsync(conn, epoch, [_a, _b]);
    await _dueScheduleAsync(conn, (Guid)TrackedGuid.New(), MOVED, "10 minutes");

    var forB = await _claimer(_b).GetNextFireTimeAsync();
    var forA = await _claimer(_a).GetNextFireTimeAsync();

    await Assert.That(forB.HasValue).IsTrue()
      .Because("B owns the schedule's partition; with no wake armed it fires only at backstop latency");
    await Assert.That(forA.HasValue).IsFalse();
  }

  [Test]
  public async Task InMapMode_TheDuePoll_RingsForTheOwnedPartitionsSchedule_WithNoLedgerRowAsync() {
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _publishAsync(conn, epoch, [_a, _b]);
    await _dueScheduleAsync(conn, (Guid)TrackedGuid.New(), MOVED);

    await Assert.That(await _pollRingsAsync(_b)).IsTrue();
    await Assert.That(await _pollRingsAsync(_a)).IsFalse();
  }

  private static Task _dueScheduleAsync(NpgsqlConnection conn, Guid stream, int partition, string fireOffset = "-1 second") =>
    _execAsync(conn, """
      INSERT INTO wh_schedules (schedule_id, stream_id, partition_number, recurrence_kind, next_fire_at, status, event_type)
      VALUES (gen_random_uuid(), @stream, @partition, 0, NOW() + @off::interval, 0, 'TestOccurrence')
      """, ("stream", stream), ("partition", partition), ("off", fireOffset));

  private PgScheduleClaimer _claimer(Guid instance) =>
    new(Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      new ServiceInstanceProvider(instance, "test", "test-host", processId: 1),
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new TemporalOptions()),
      NullLogger<PgScheduleClaimer>.Instance);

  private async Task<bool> _pollRingsAsync(Guid instance) {
    var source = new PgScheduleDuePollSource(
      new FakeTimeProvider(), Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      new ServiceInstanceProvider(instance, "test", "test-host", processId: 1),
      NullLogger<PgScheduleDuePollSource>.Instance);
    var sink = new RingSink();
    await source.StartAsync(sink);
    await source.TickForTestsAsync(CancellationToken.None);
    return sink.Rang;
  }

  private sealed class RingSink : ISignalSink {
    public bool Rang { get; private set; }

    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Rang = true;
      return ValueTask.CompletedTask;
    }
  }
}
