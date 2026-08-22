using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PgScheduleDuePollSource"/> — the temporal-engine backstop pull
/// source. Its detection query raises <see cref="ScheduleDueSignal"/> exactly when an Active schedule
/// owned by this pod's instance is due (<c>next_fire_at &lt;= NOW()</c>), and not otherwise.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
[Category("Shard4")]
public class PgScheduleDuePollSourceIntegrationTests : EFCoreTestBase {
  private sealed class CountingSink : ISignalSink {
    public int Received { get; private set; }
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Received++;
      return ValueTask.CompletedTask;
    }
  }

  private (PgScheduleDuePollSource Source, IServiceInstanceProvider Instance) _createSource() {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "utest-svc", "utest-host", processId: 1);
    var source = new PgScheduleDuePollSource(
      TimeProvider.System, Options.Create(opts), cfg, instance,
      NullLogger<PgScheduleDuePollSource>.Instance);
    return (source, instance);
  }

  private async Task _pinStreamToInstanceAsync(Guid streamId, Guid instanceId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      VALUES (@stream_id, 0, @instance_id, NOW(), NOW())
      ON CONFLICT (stream_id) DO UPDATE
        SET assigned_instance_id = EXCLUDED.assigned_instance_id, last_activity_at = NOW();", conn);
    cmd.Parameters.AddWithValue("stream_id", streamId);
    cmd.Parameters.AddWithValue("instance_id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  // next_fire_at is computed relative to the DB clock (NOW() + the given interval), NOT the C# host clock —
  // the poll source's due check is `next_fire_at <= NOW()` on the DB clock, so seeding from the host clock
  // makes the "due" tests hostage to host↔container clock skew (a Docker VM clock can drift minutes behind the
  // host under load, so a "-1 minute (host)" seed reads as future to the DB and the schedule is spuriously
  // not-due). Seeding on the engine's own clock is drift-proof.
  private async Task _insertScheduleAsync(Guid streamId, string fireOffset, short status) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_schedules
        (schedule_id, stream_id, partition_number, recurrence_kind, next_fire_at, status, event_type)
      VALUES (gen_random_uuid(), @stream, 0, 0, NOW() + @off::interval, @status, 'TestOccurrence');", conn);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("off", fireOffset);
    cmd.Parameters.AddWithValue("status", status);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task DueScheduleForMyInstance_TickRaisesAsync() {
    var (source, instance) = _createSource();
    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);
    await _insertScheduleAsync(streamId, "-1 minute", status: 0);

    var sink = new CountingSink();
    await source.StartAsync(sink);
    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.Received).IsEqualTo(1);
  }

  [Test]
  public async Task NoSchedule_TickDoesNotRaiseAsync() {
    var (source, _) = _createSource();
    var sink = new CountingSink();
    await source.StartAsync(sink);

    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.Received).IsEqualTo(0);
  }

  [Test]
  public async Task FutureSchedule_TickDoesNotRaiseAsync() {
    var (source, instance) = _createSource();
    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);
    await _insertScheduleAsync(streamId, "1 hour", status: 0);

    var sink = new CountingSink();
    await source.StartAsync(sink);
    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.Received).IsEqualTo(0)
      .Because("a schedule whose next_fire_at is in the future is not due");
  }

  [Test]
  public async Task DueScheduleForOtherInstance_TickDoesNotRaiseAsync() {
    var (source, _) = _createSource();
    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, Guid.NewGuid());   // owned by a DIFFERENT instance
    await _insertScheduleAsync(streamId, "-1 minute", status: 0);

    var sink = new CountingSink();
    await source.StartAsync(sink);
    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.Received).IsEqualTo(0)
      .Because("the poll source must scope detection to its own instance");
  }

  [Test]
  public async Task PausedDueSchedule_TickDoesNotRaiseAsync() {
    var (source, instance) = _createSource();
    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);
    await _insertScheduleAsync(streamId, "-1 minute", status: 1);   // Paused

    var sink = new CountingSink();
    await source.StartAsync(sink);
    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.Received).IsEqualTo(0)
      .Because("a paused schedule must not fire even when due");
  }
}
