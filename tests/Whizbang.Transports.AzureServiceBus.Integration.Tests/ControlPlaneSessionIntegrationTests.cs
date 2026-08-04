using Azure.Messaging.ServiceBus;
using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// The production-topology lock for the integrity control plane: session-REQUIRED subscriptions
/// (per-stream FIFO deployments) dead-letter every message that carries no session id. A direct
/// transport publish through <see cref="ControlPlaneDestination"/> must therefore deliver, and the
/// bare-destination publish it replaced must land in the dead-letter queue — the exact live
/// failure (checkpoints broker-dead-lettered by the thousands, consumers never processing one)
/// that this pins in CI against the real broker emulator.
/// </summary>
[Timeout(240_000)]
[Category("Integration")]
[NotInParallel("ServiceBus")]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class ControlPlaneSessionIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;
  private readonly List<IAsyncDisposable> _disposables = [];

  [After(Test)]
  public async Task DisposeTrackedTransportsAsync() {
    foreach (var d in _disposables) {
      try { await d.DisposeAsync(); } catch { /* best-effort cleanup */ }
    }
    _disposables.Clear();
  }

  [Test]
  public async Task ControlPlanePublish_SessionRequiredSubscription_DeliversAsync() {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new AzureServiceBusOptions { EnableSessions = true, AutoProvisionInfrastructure = true };
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);
    _disposables.Add(transport);
    await transport.InitializeAsync();

    var checkpoint = new IntegrityCheckpoint {
      CheckpointStreamId = TrackedGuid.NewMedo().Value,
      OriginServiceId = TrackedGuid.NewMedo().Value,
      OriginServiceName = "origin-svc",
      FromCommitSequence = 1,
      ToCommitSequence = 2,
      Buckets = [],
    };
    var envelope = new MessageEnvelope<IntegrityCheckpoint> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = checkpoint,
      Hops = [],
      DispatchContext = new MessageDispatchContext {
        Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox,
        Source = MessageSource.Outbox,
      },
    };
    var serializer = new EnvelopeSerializer(jsonOptions);
    var serialized = serializer.SerializeEnvelope(envelope);

    var receivedChannel = Channel.CreateUnbounded<Guid>();
    var subscription = await transport.SubscribeAsync(
      async (received, _, ct) => {
        await receivedChannel.Writer.WriteAsync(received.MessageId.Value, ct);
      },
      new TransportDestination("topic-fifo-01", "sub-fifo-session")
    );

    try {
      await transport.PublishAsync(
        serialized.JsonEnvelope,
        ControlPlaneDestination.For("topic-fifo-01", checkpoint.CheckpointStreamId),
        serialized.EnvelopeType,
        cancellationToken: CancellationToken.None);

      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
      Guid received;
      do {
        received = await receivedChannel.Reader.ReadAsync(cts.Token);
      } while (received != envelope.MessageId.Value);

      await Assert.That(received).IsEqualTo(envelope.MessageId.Value)
        .Because("a control-plane publish carrying its session key must reach a session-REQUIRED " +
                 "subscription — the exact production topology where sessionless publishes were " +
                 "silently broker-dead-lettered.");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task BarePublish_SessionRequiredSubscription_IsDeadLetteredAsync() {
    // The pre-fix shape: a direct publish with NO session metadata. The broker must dead-letter
    // it on a session-required subscription — asserting the DLQ arrival is the deterministic
    // positive signal (never a negative timeout on the main queue).
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new AzureServiceBusOptions { EnableSessions = true, AutoProvisionInfrastructure = true };
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);
    _disposables.Add(transport);
    await transport.InitializeAsync();

    var envelope = new MessageEnvelope<IntegrityCheckpoint> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new IntegrityCheckpoint {
        CheckpointStreamId = TrackedGuid.NewMedo().Value,
        OriginServiceId = TrackedGuid.NewMedo().Value,
        OriginServiceName = "origin-svc",
        FromCommitSequence = 1,
        ToCommitSequence = 1,
        Buckets = [],
      },
      Hops = [],
      DispatchContext = new MessageDispatchContext {
        Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox,
        Source = MessageSource.Outbox,
      },
    };
    var serializer = new EnvelopeSerializer(jsonOptions);
    var serialized = serializer.SerializeEnvelope(envelope);

    await transport.PublishAsync(
      serialized.JsonEnvelope,
      new TransportDestination("topic-fifo-02"),
      serialized.EnvelopeType,
      cancellationToken: CancellationToken.None);

    await using var dlqReceiver = _fixture.Client.CreateReceiver(
      "topic-fifo-02", "sub-fifo-session",
      new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    ServiceBusReceivedMessage? dead = null;
    while (dead is null && !cts.IsCancellationRequested) {
      var candidate = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10), cts.Token);
      if (candidate is null) {
        continue;
      }
      if (candidate.MessageId == envelope.MessageId.Value.ToString()) {
        dead = candidate;
      }
      await dlqReceiver.CompleteMessageAsync(candidate, cts.Token);
    }

    await Assert.That(dead).IsNotNull()
      .Because("a sessionless publish to a session-REQUIRED subscription is broker-dead-lettered — " +
               "the live failure mode this suite exists to keep impossible to reintroduce silently.");
  }
}
