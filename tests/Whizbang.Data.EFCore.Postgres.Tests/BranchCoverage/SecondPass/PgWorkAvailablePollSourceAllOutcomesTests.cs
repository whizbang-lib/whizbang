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
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// Every outcome of the work-available poll source's decisions in one class: the options guard, a tick
/// with no connection configured, and ticks against a real database that find no work and that find
/// work owned by this instance.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgWorkAvailablePollSourceBase.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class PgWorkAvailablePollSourceAllOutcomesTests : EFCoreTestBase {

  [Test]
  public async Task Constructor_WithNoOptionsOrNoOptionsValue_NamesOptionsAsync() {
    var instance = _instance();

    var noOptions = await Assert.That(() => new PgOutboxWorkAvailablePollSource(
        new FakeTimeProvider(), null!, new ConfigurationBuilder().Build(), instance,
        NullLogger<PgOutboxWorkAvailablePollSource>.Instance))
      .Throws<ArgumentNullException>();
    var noValue = await Assert.That(() => new PgOutboxWorkAvailablePollSource(
        new FakeTimeProvider(), Options.Create<WhizbangNotificationOptions>(null!), new ConfigurationBuilder().Build(), instance,
        NullLogger<PgOutboxWorkAvailablePollSource>.Instance))
      .Throws<ArgumentNullException>();

    await Assert.That(noOptions!.ParamName).IsEqualTo("options");
    await Assert.That(noValue!.ParamName).IsEqualTo("options");
  }

  // No connection configured: nothing to detect, so no signal.
  [Test]
  [Timeout(60000)]
  public async Task Tick_WithNoConnectionConfigured_RaisesNothingAsync(CancellationToken cancellationToken) {
    var sink = new CountingSink();
    var source = _source(connectionString: null, _instance());
    await source.StartAsync(sink, cancellationToken);

    await source.TickForTestsAsync(cancellationToken);

    await Assert.That(sink.Received).IsEqualTo(0);
  }

  // A database with no work for this instance answers false: no signal.
  [Test]
  [Timeout(60000)]
  public async Task Tick_WithADatabaseHoldingNoWork_RaisesNothingAsync(CancellationToken cancellationToken) {
    var sink = new CountingSink();
    var source = _source(ConnectionString, _instance());
    await source.StartAsync(sink, cancellationToken);

    await source.TickForTestsAsync(cancellationToken);

    await Assert.That(sink.Received).IsEqualTo(0);
  }

  // Unprocessed outbox work on a stream this instance owns answers true: the doorbell rings.
  [Test]
  [Timeout(60000)]
  public async Task Tick_WithOwnedOutboxWork_RaisesTheSignalAsync(CancellationToken cancellationToken) {
    var instance = _instance();
    var streamId = Guid.NewGuid();
    await _seedOwnedOutboxAsync(streamId, instance.InstanceId, cancellationToken);
    var sink = new CountingSink();
    var source = _source(ConnectionString, instance);
    await source.StartAsync(sink, cancellationToken);

    await source.TickForTestsAsync(cancellationToken);

    await Assert.That(sink.Received).IsEqualTo(1);
  }

  private static ServiceInstanceProvider _instance() =>
    new(Guid.NewGuid(), "poll-all-outcomes-svc", "poll-all-outcomes-host", processId: 1);

  private static PgOutboxWorkAvailablePollSource _source(string? connectionString, ServiceInstanceProvider instance) => new(
    new FakeTimeProvider(),
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = connectionString }),
    new ConfigurationBuilder().Build(),
    instance,
    NullLogger<PgOutboxWorkAvailablePollSource>.Instance);

  private async Task _seedOwnedOutboxAsync(Guid streamId, Guid instanceId, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      VALUES (@stream, 0, @instance, NOW(), NOW());
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
      VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, NOW(), @stream, 0);", conn);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("instance", instanceId);
    cmd.Parameters.AddWithValue("msg", Guid.NewGuid());
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private sealed class CountingSink : ISignalSink {
    private int _received;

    public int Received => Volatile.Read(ref _received);

    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
        where TSignal : ISignal {
      Interlocked.Increment(ref _received);
      return ValueTask.CompletedTask;
    }
  }
}
