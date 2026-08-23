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
/// Integration tests for the Postgres work-available pull sources — each source's detection
/// query must return true when an unclaimed row exists for the pod's instance in the target
/// table, and false otherwise. Verifies the SQL against a real database and the SignalBus
/// dispatch through <c>TickForTestsAsync</c>.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[Category("Shard3")]
public class PgWorkAvailablePollSourceIntegrationTests : EFCoreTestBase {
  private sealed class CountingSink : ISignalSink {
    public int Received { get; private set; }
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Received++;
      return ValueTask.CompletedTask;
    }
  }

  private (PgOutboxWorkAvailablePollSource Source, IServiceInstanceProvider Instance) _createOutboxSource() {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "utest-svc", "utest-host", processId: 1);
    var source = new PgOutboxWorkAvailablePollSource(
      TimeProvider.System, Options.Create(opts), cfg, instance,
      NullLogger<PgOutboxWorkAvailablePollSource>.Instance);
    return (source, instance);
  }

  private (PgInboxWorkAvailablePollSource Source, IServiceInstanceProvider Instance) _createInboxSource() {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "utest-svc", "utest-host", processId: 1);
    var source = new PgInboxWorkAvailablePollSource(
      TimeProvider.System, Options.Create(opts), cfg, instance,
      NullLogger<PgInboxWorkAvailablePollSource>.Instance);
    return (source, instance);
  }

  private (PgPerspectiveWorkAvailablePollSource Source, IServiceInstanceProvider Instance) _createPerspectiveSource() {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "utest-svc", "utest-host", processId: 1);
    var source = new PgPerspectiveWorkAvailablePollSource(
      TimeProvider.System, Options.Create(opts), cfg, instance,
      NullLogger<PgPerspectiveWorkAvailablePollSource>.Instance);
    return (source, instance);
  }

  private async Task _pinStreamToInstanceAsync(Guid streamId, Guid instanceId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      VALUES (@stream_id, 0, @instance_id, NOW(), NOW())
      ON CONFLICT (stream_id) DO UPDATE
        SET assigned_instance_id = EXCLUDED.assigned_instance_id,
            last_activity_at = NOW();", conn);
    cmd.Parameters.AddWithValue("stream_id", streamId);
    cmd.Parameters.AddWithValue("instance_id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _insertOutboxRowAsync(Guid streamId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_outbox (message_id, message_type, event_data, metadata, status, created_at, stream_id, partition_number, failure_reason)
      VALUES (gen_random_uuid(), 'utest-msg', '{}'::jsonb, '{}'::jsonb, 0, NOW(), @stream_id, 0, 0);", conn);
    cmd.Parameters.AddWithValue("stream_id", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _insertInboxRowAsync(Guid streamId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, received_at, stream_id, partition_number, failure_reason)
      VALUES (gen_random_uuid(), 'utest-handler', 'utest-msg', '{}'::jsonb, '{}'::jsonb, 0, NOW(), @stream_id, 0, 0);", conn);
    cmd.Parameters.AddWithValue("stream_id", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task Outbox_NoWork_TickDoesNotRaiseAsync() {
    var (source, _) = _createOutboxSource();
    var sink = new CountingSink();
    await source.StartAsync(sink);

    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.Received).IsEqualTo(0);
  }

  [Test]
  public async Task Outbox_WorkForMyInstance_TickRaisesAsync() {
    var (source, instance) = _createOutboxSource();
    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);
    await _insertOutboxRowAsync(streamId);

    var sink = new CountingSink();
    await source.StartAsync(sink);
    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.Received).IsEqualTo(1);
  }

  [Test]
  public async Task Outbox_WorkForOtherInstance_TickDoesNotRaiseAsync() {
    var (source, _) = _createOutboxSource();
    var otherInstance = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, otherInstance);
    await _insertOutboxRowAsync(streamId);

    var sink = new CountingSink();
    await source.StartAsync(sink);
    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.Received).IsEqualTo(0)
      .Because("the poll source must scope detection to its own instance; other instances' work is not this pod's business");
  }

  [Test]
  public async Task Inbox_WorkForMyInstance_TickRaisesAsync() {
    var (source, instance) = _createInboxSource();
    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);
    await _insertInboxRowAsync(streamId);

    var sink = new CountingSink();
    await source.StartAsync(sink);
    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.Received).IsEqualTo(1);
  }

  [Test]
  public async Task Inbox_NoWork_TickDoesNotRaiseAsync() {
    var (source, _) = _createInboxSource();
    var sink = new CountingSink();
    await source.StartAsync(sink);

    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.Received).IsEqualTo(0);
  }

  [Test]
  public async Task Perspective_NoWork_TickDoesNotRaiseAsync() {
    var (source, _) = _createPerspectiveSource();
    var sink = new CountingSink();
    await source.StartAsync(sink);

    await source.TickForTestsAsync(CancellationToken.None);

    await Assert.That(sink.Received).IsEqualTo(0);
  }
}
