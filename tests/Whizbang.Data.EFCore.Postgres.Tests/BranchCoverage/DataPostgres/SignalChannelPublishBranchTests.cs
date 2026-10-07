// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Notifications.AppSignals;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// The publish paths' remaining decisions against a real server: a durable signal aimed at one
/// instance, and an application signal published with no payload.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresSignalTransport.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgAppSignalChannel.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class SignalChannelPublishBranchTests : EFCoreTestBase {
  private readonly record struct BranchDurableTargetedProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
    public static SignalTargeting Targeting => SignalTargeting.Targeted;
  }

  private sealed class FakeSource(IReadOnlyList<SignalTypeEntry> entries) : ISignalTypeSource {
    public IReadOnlyList<SignalTypeEntry> GetSignalTypes() => entries;
  }

  private sealed class NoopSink : ISignalSink {
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
        where TSignal : ISignal => ValueTask.CompletedTask;
  }

  // A durable signal for one instance is replayed only by that instance's tail; persisting it with
  // no target would have every instance's tail deliver it, which is a broadcast the publisher never
  // asked for.
  [Test]
  [Timeout(60000)]
  public async Task DurableSignal_TargetedAtAnInstance_PersistsThatInstanceAsTheTargetAsync(CancellationToken cancellationToken) {
    const string wireName = "branch-durable-targeted-61423";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(BranchDurableTargetedProbe), wireName,
        SignalDeliveryClass.Durable, SignalTargeting.Targeted,
        static (sink, ct) => sink.ReceiveAsync<BranchDurableTargetedProbe>(default, ct)),
    ]));
    var options = Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString });
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "branch-svc", "branch-host", processId: 1);
    using var shared = new PgSharedNotifyConnection(options, configuration, instance);
    using var transport = new PostgresSignalTransport(
      options, configuration, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    await transport.StartAsync(new NoopSink(), cancellationToken);
    var target = Guid.NewGuid();

    await transport.PublishAsync(new BranchDurableTargetedProbe(1), SignalTarget.Instance(target), cancellationToken);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(
      "SELECT target_instance_id FROM wh_signals WHERE wire_name = @w ORDER BY id DESC LIMIT 1", conn);
    cmd.Parameters.AddWithValue("w", wireName);
    var persisted = await cmd.ExecuteScalarAsync(cancellationToken);

    await Assert.That(persisted).IsEqualTo(target);
  }

  // A null payload is sent as an empty one. Handing null to the driver as a parameter value fails
  // the publish outright, and a topic used as a bare doorbell has nothing to say in its payload.
  [Test]
  [Timeout(60000)]
  public async Task AppSignal_PublishedWithoutAPayload_ArrivesWithAnEmptyPayloadAsync(CancellationToken cancellationToken) {
    const string topic = "branch_null_payload";
    var channel = AppSignalTopicValidator.ToChannelName(topic);
    await using var listener = new NpgsqlConnection(ConnectionString);
    await listener.OpenAsync(cancellationToken);
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    listener.Notification += (_, e) => {
      if (e.Channel == channel) {
        received.TrySetResult(e.Payload);
      }
    };
    await using (var listen = new NpgsqlCommand($"LISTEN \"{channel}\"", listener)) {
      await listen.ExecuteNonQueryAsync(cancellationToken);
    }
    var appChannel = new PgAppSignalChannel(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      new NullSharedConnection(),
      NullLogger<PgAppSignalChannel>.Instance);

    await appChannel.PublishAsync(topic, null!, cancellationToken);
    while (!received.Task.IsCompleted) {
      // Blocks until the server delivers an asynchronous message; the handler above runs inside it.
      await listener.WaitAsync(cancellationToken);
    }

    await Assert.That(await received.Task).IsEqualTo(string.Empty);
  }

  private sealed class NullSharedConnection : ISharedNotifyConnection {
    public IDisposable Subscribe(INotifySubscription subscription) =>
      throw new NotSupportedException("publishing does not subscribe");
  }
}
