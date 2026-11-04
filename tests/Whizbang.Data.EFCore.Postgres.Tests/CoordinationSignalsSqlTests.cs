// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2046: the database side of the coordination channels on the signal bus. One SQL function, the bus's
/// (<c>wh_publish_signal</c>), notifies; a decision is also recorded in the durable signal log, with its payload, so an
/// instance that missed the notification is told by its tail; a doorbell is never recorded. The channel and payload an
/// older release listens for are unchanged, so a mixed fleet keeps hearing each other.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[Category("Shard2")]
public class CoordinationSignalsSqlTests : EFCoreTestBase {
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(30);

  private sealed record SignalRow(string WireName, string? Payload, Guid? Target);

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private async Task<long> _maxSignalIdAsync(CancellationToken ct) {
    await using var conn = await _openAsync(ct);
    await using var cmd = new NpgsqlCommand("SELECT COALESCE(MAX(id), 0) FROM wh_signals", conn);
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private async Task<List<SignalRow>> _signalsAfterAsync(long id, CancellationToken ct) {
    await using var conn = await _openAsync(ct);
    await using var cmd = new NpgsqlCommand(
      "SELECT wire_name, payload, target_instance_id FROM wh_signals WHERE id > @id ORDER BY id", conn);
    cmd.Parameters.AddWithValue(nameof(id), id);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    var rows = new List<SignalRow>();
    while (await reader.ReadAsync(ct)) {
      rows.Add(new SignalRow(reader.GetString(0), await reader.IsDBNullAsync(1, ct) ? null : reader.GetString(1),
        await reader.IsDBNullAsync(2, ct) ? null : reader.GetGuid(2)));
    }
    return rows;
  }

  /// <summary>Opens a raw LISTEN on <paramref name="channel"/>, as an instance on an older release does.</summary>
  private async Task<(NpgsqlConnection Listener, List<string> Payloads)> _listenAsync(string channel, CancellationToken ct) {
    var listener = await _openAsync(ct);
    var payloads = new List<string>();
    listener.Notification += (_, e) => {
      if (e.Channel == channel) {
        payloads.Add(e.Payload);
      }
    };
    await using var listen = new NpgsqlCommand($"LISTEN \"{channel}\"", listener);
    await listen.ExecuteNonQueryAsync(ct);
    return (listener, payloads);
  }

  private static async Task _awaitNotificationAsync(NpgsqlConnection listener, CancellationToken ct) {
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
    deadline.CancelAfter(_signalDeadline);
    await listener.WaitAsync(deadline.Token);
  }

  [Test]
  public async Task PublishSignal_ADecision_IsRecordedWithItsPayload_AndNotifiedAtCommitAsync(CancellationToken cancellationToken) {
    var before = await _maxSignalIdAsync(cancellationToken);
    var (listener, payloads) = await _listenAsync(PgSignalWire.ROLE_RELEASED_CHANNEL, cancellationToken);
    await using var listening = listener;

    await using (var conn = await _openAsync(cancellationToken))
    await using (var cmd = new NpgsqlCommand("SELECT wh_publish_signal(@channel, @payload, @wire)", conn)) {
      cmd.Parameters.AddWithValue("channel", PgSignalWire.ROLE_RELEASED_CHANNEL);
      cmd.Parameters.AddWithValue("payload", "whizbang:maintainer");
      cmd.Parameters.AddWithValue("wire", "role-released");
      _ = await cmd.ExecuteScalarAsync(cancellationToken);
    }
    await _awaitNotificationAsync(listener, cancellationToken);

    await Assert.That(payloads).IsEquivalentTo(["whizbang:maintainer"]);
    await Assert.That(await _signalsAfterAsync(before, cancellationToken))
      .IsEquivalentTo([new SignalRow("role-released", "whizbang:maintainer", null)]);
  }

  [Test]
  public async Task PublishSignal_ADoorbell_IsNotifiedButNeverRecordedAsync(CancellationToken cancellationToken) {
    var before = await _maxSignalIdAsync(cancellationToken);
    var (listener, payloads) = await _listenAsync(PgSignalWire.COMMITTED_CHANNEL, cancellationToken);
    await using var listening = listener;

    await using (var conn = await _openAsync(cancellationToken))
    await using (var cmd = new NpgsqlCommand("SELECT wh_publish_signal(@channel, @payload)", conn)) {
      cmd.Parameters.AddWithValue("channel", PgSignalWire.COMMITTED_CHANNEL);
      cmd.Parameters.AddWithValue("payload", string.Empty);
      _ = await cmd.ExecuteScalarAsync(cancellationToken);
    }
    await _awaitNotificationAsync(listener, cancellationToken);

    await Assert.That(payloads).IsEquivalentTo([string.Empty]);
    await Assert.That(await _signalsAfterAsync(before, cancellationToken)).IsEmpty()
      .Because("a doorbell has a poll behind it; a durable row per ring would be a write on the hot path");
  }

  [Test]
  public async Task PublishSignal_InsideATransactionThatRollsBack_LeavesNoTraceAsync(CancellationToken cancellationToken) {
    var before = await _maxSignalIdAsync(cancellationToken);
    await using var conn = await _openAsync(cancellationToken);
    await using (var tx = await conn.BeginTransactionAsync(cancellationToken)) {
      await using var cmd = new NpgsqlCommand("SELECT wh_publish_signal(@channel, @payload, @wire)", conn, tx);
      cmd.Parameters.AddWithValue("channel", PgSignalWire.ROLE_RELEASED_CHANNEL);
      cmd.Parameters.AddWithValue("payload", "whizbang:maintainer");
      cmd.Parameters.AddWithValue("wire", "role-released");
      _ = await cmd.ExecuteScalarAsync(cancellationToken);
      await tx.RollbackAsync(cancellationToken);
    }

    await Assert.That(await _signalsAfterAsync(before, cancellationToken)).IsEmpty()
      .Because("a decision is announced only by the transaction that made it");
  }

  [Test]
  public async Task RecordingAMultiStreamRun_RingsTheOlderChannelWithTheRunId_AndRecordsNothingAsync(CancellationToken cancellationToken) {
    var before = await _maxSignalIdAsync(cancellationToken);
    var (listener, payloads) = await _listenAsync(PgSignalWire.MULTI_STREAM_WORK_CHANNEL, cancellationToken);
    await using var listening = listener;
    var run = Guid.CreateVersion7();

    await using (var conn = await _openAsync(cancellationToken))
    await using (var cmd = new NpgsqlCommand(
        "SELECT wh_record_multi_stream_run(@run, 'Probe', '{}', 'streams', @streams, 16)", conn)) {
      cmd.Parameters.AddWithValue("run", run);
      cmd.Parameters.Add(new NpgsqlParameter("streams", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { Guid.CreateVersion7() } });
      _ = await cmd.ExecuteScalarAsync(cancellationToken);
    }
    await _awaitNotificationAsync(listener, cancellationToken);

    await Assert.That(payloads).IsEquivalentTo([run.ToString()]);
    await Assert.That(await _signalsAfterAsync(before, cancellationToken)).IsEmpty();
  }

  /// <summary>
  /// The decision a subscriber missed is replayed by its tail, payload and all: the release reached the database, the
  /// notification was lost (this instance was not listening), and the next tick tells the subscriber which role.
  /// </summary>
  [Test]
  public async Task TheTail_ReplaysAMissedDecision_WithItsPayloadAsync(CancellationToken cancellationToken) {
    var instance = Guid.CreateVersion7();
    var sink = new RecordingSink();
    var options = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var tail = new PgDurableSignalTailWorker(Options.Create(options), configuration,
      new ServiceInstanceProvider(instance, "tail-svc", "tail-host", processId: 1), sink, NullLogger<PgDurableSignalTailWorker>.Instance);
    tail.UseWireMap(SignalTypeRegistry.GetAll().ToDictionary(e => e.WireName, StringComparer.Ordinal));
    var plan = NotificationConnectionPlan.Create(null, NotificationConnectionStringResolver.Resolve(options, configuration));
    await using (var conn = await _openAsync(cancellationToken))
    await using (var cursor = new NpgsqlCommand(
        "INSERT INTO wh_signal_cursors (instance_id, last_delivered_signal_id) VALUES (@id, (SELECT COALESCE(MAX(id), 0) FROM wh_signals))", conn)) {
      cursor.Parameters.AddWithValue("id", instance);
      _ = await cursor.ExecuteNonQueryAsync(cancellationToken);
    }
    await using (var conn = await _openAsync(cancellationToken))
    await using (var publish = new NpgsqlCommand(
        "SELECT wh_publish_signal('wh_role_released', 'whizbang:maintainer', 'role-released'), "
        + "wh_publish_signal('wh_partition_assignment', '7:3', 'partition-assignment-published')", conn)) {
      _ = await publish.ExecuteScalarAsync(cancellationToken);
    }

    var delivered = await tail.TickOnceAsync(plan, cancellationToken);

    await Assert.That(delivered).IsEqualTo(2);
    await Assert.That(sink.Received).IsEquivalentTo(new object[] {
      new RoleReleasedSignal("whizbang:maintainer"),
      new PartitionAssignmentPublishedSignal("7:3"),
    }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  /// <summary>
  /// A decision published through the bus from C# takes the same path: recorded durably with its payload, and notified
  /// on the channel an older release listens on.
  /// </summary>
  [Test]
  public async Task PublishingADecisionOnTheBus_RecordsIt_AndRingsTheOlderChannelAsync(CancellationToken cancellationToken) {
    var before = await _maxSignalIdAsync(cancellationToken);
    var (listener, payloads) = await _listenAsync(PgSignalWire.ROLE_RELEASED_CHANNEL, cancellationToken);
    await using var listening = listener;
    await using var stack = PgSignalBusStack.Create(ConnectionString);
    await stack.Transport.StartAsync(new RecordingSink(), cancellationToken);

    await stack.Transport.PublishAsync(new RoleReleasedSignal("whizbang:stamper"), SignalTarget.Broadcast, cancellationToken);
    await _awaitNotificationAsync(listener, cancellationToken);

    await Assert.That(payloads).IsEquivalentTo(["whizbang:stamper"]);
    await Assert.That(await _signalsAfterAsync(before, cancellationToken))
      .IsEquivalentTo([new SignalRow("role-released", "whizbang:stamper", null)]);
  }

  /// <summary>A doorbell published through the bus rings its channel and is not recorded.</summary>
  [Test]
  public async Task PublishingADoorbellOnTheBus_RingsTheOlderChannel_AndRecordsNothingAsync(CancellationToken cancellationToken) {
    var before = await _maxSignalIdAsync(cancellationToken);
    var (listener, payloads) = await _listenAsync(PgSignalWire.MULTI_STREAM_WORK_CHANNEL, cancellationToken);
    await using var listening = listener;
    await using var stack = PgSignalBusStack.Create(ConnectionString);
    await stack.Transport.StartAsync(new RecordingSink(), cancellationToken);

    await stack.Transport.PublishAsync(new MultiStreamWorkSignal(), SignalTarget.Broadcast, cancellationToken);
    await _awaitNotificationAsync(listener, cancellationToken);

    await Assert.That(payloads).IsEquivalentTo([string.Empty]);
    await Assert.That(await _signalsAfterAsync(before, cancellationToken)).IsEmpty();
  }

  /// <summary>
  /// One instance's release is heard by another instance's bus subscriber: the decision crosses the fleet on the bus.
  /// </summary>
  [Test]
  public async Task ADecision_OnOneInstancesBus_ReachesAnotherInstancesSubscriberAsync(CancellationToken cancellationToken) {
    await using var publisher = PgSignalBusStack.Create(ConnectionString);
    await using var subscriber = PgSignalBusStack.Create(ConnectionString);
    var heard = new TaskCompletionSource<RoleReleasedSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subscription = subscriber.Bus.Subscribe<RoleReleasedSignal>(s => {
      heard.TrySetResult(s);
      return ValueTask.CompletedTask;
    });
    await subscriber.StartAsync(cancellationToken);
    await subscriber.ListenedAsync(PgSignalWire.ROLE_RELEASED_CHANNEL, cancellationToken);
    await publisher.Transport.StartAsync(new RecordingSink(), cancellationToken);

    await publisher.Bus.PublishAsync(new RoleReleasedSignal("whizbang:partition-assigner"), cancellationToken: cancellationToken);

    await Assert.That((await heard.Task.WaitAsync(_signalDeadline, cancellationToken)).Role).IsEqualTo("whizbang:partition-assigner");
  }

  private sealed class RecordingSink : ISignalSink {
    private readonly Lock _gate = new();
    private readonly List<object> _received = [];

    public List<object> Received {
      get {
        lock (_gate) {
          return [.. _received];
        }
      }
    }

    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default) where TSignal : ISignal {
      lock (_gate) {
        _received.Add(signal!);
      }
      return ValueTask.CompletedTask;
    }
  }
}
