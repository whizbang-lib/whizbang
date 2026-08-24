using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
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
  public async Task ControlPlanePublish_FilteredSubscription_SubjectAdmits_NoSubjectFilteredOutAsync() {
    // Production shared-inbox subscriptions filter on the message Subject (sys.Label) by
    // namespace. A typed ControlPlaneDestination publish carries
    // "whizbang.core.messaging.<type>" and passes; the untyped publish (Subject "message") is
    // silently dropped by the broker rule — no delivery, no dead-letter (the exact live failure:
    // requests fired every cycle, zero receipts anywhere). Ordering proves the drop: the
    // admitted message is published SECOND on the SAME session yet arrives; the filtered one
    // never does.
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new AzureServiceBusOptions { EnableSessions = true, AutoProvisionInfrastructure = true };
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);
    _disposables.Add(transport);
    await transport.InitializeAsync();

    var sessionId = TrackedGuid.NewMedo().Value;
    var serializer = new EnvelopeSerializer(jsonOptions);
    MessageEnvelope<IntegrityCheckpoint> _mk() => new() {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new IntegrityCheckpoint {
        CheckpointStreamId = sessionId,
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

    var received = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    var subscription = await transport.SubscribeAsync(
      async (env, _, ct) => { await received.Writer.WriteAsync(env.MessageId.Value, ct); },
      new TransportDestination("topic-filtered-01", "sub-filtered-session"));

    try {
      var filteredEnvelope = _mk();
      var filteredSerialized = serializer.SerializeEnvelope(filteredEnvelope);
      await transport.PublishAsync(
        filteredSerialized.JsonEnvelope,
        ControlPlaneDestination.For("topic-filtered-01", sessionId),   // no Subject → filtered out
        filteredSerialized.EnvelopeType, cancellationToken: CancellationToken.None);

      var admittedEnvelope = _mk();
      var admittedSerialized = serializer.SerializeEnvelope(admittedEnvelope);
      await transport.PublishAsync(
        admittedSerialized.JsonEnvelope,
        ControlPlaneDestination.For("topic-filtered-01", sessionId, typeof(IntegrityCheckpoint)),
        admittedSerialized.EnvelopeType, cancellationToken: CancellationToken.None);

      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
      var first = await received.Reader.ReadAsync(cts.Token);
      await Assert.That(first).IsEqualTo(admittedEnvelope.MessageId.Value)
        .Because("same session, published SECOND, yet arrives FIRST — the broker rule dropped " +
                 "the untyped publish before it, exactly the live silent-drop failure mode.");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task StreamlessPublish_SessionRequiredSubscription_IsDeliveredNotDeadLetteredAsync() {
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

    // Proving ABSENCE needs different mechanics from proving presence. The original loop polled
    // until a long token fired, which was fine while a dead-letter was guaranteed to arrive; now
    // that none ever does, spinning to the deadline just kills the test with TaskCanceledException
    // before it reaches its assertion. Drain what is actually there, stop when the queue is empty,
    // and treat the deadline as the expected outcome rather than a failure.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    ServiceBusReceivedMessage? dead = null;
    try {
      while (dead is null && !cts.IsCancellationRequested) {
        var candidate = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), cts.Token);
        if (candidate is null) {
          break;  // nothing waiting — the dead-letter queue is empty, which is the point
        }
        if (candidate.MessageId == envelope.MessageId.Value.ToString()) {
          dead = candidate;
        }
        await dlqReceiver.CompleteMessageAsync(candidate, CancellationToken.None);
      }
    } catch (OperationCanceledException) {
      // Expected. Nothing was dead-lettered within the window, which is exactly the assertion below.
    }

    // INVERTED, and this inversion IS the point of the fix. This test was written to characterise a
    // live failure: a streamless control-plane publish reached a session-REQUIRED subscription with
    // no session id and the broker dead-lettered it before any consumer saw it. The transport now
    // stamps a session id on EVERY message, so that failure can no longer be produced through the
    // publish path at all — the message is delivered instead of destroyed.
    //
    // Keeping the original assertion would lock the defect in as expected behavior. The property
    // worth guarding is the one below: the publish path must never emit a message a session-enabled
    // entity will refuse.
    await Assert.That(dead).IsNull()
      .Because("the publish path now stamps a session id on every message, so a control-plane "
             + "broadcast can no longer be broker-dead-lettered for a null session — this test "
             + "characterised the live failure, and the fix makes that failure unreachable");
  }
}
