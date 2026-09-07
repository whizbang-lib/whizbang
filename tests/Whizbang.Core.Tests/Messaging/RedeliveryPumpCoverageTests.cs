using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage for two <see cref="RedeliveryPump"/> paths <see cref="RedeliveryPumpTests"/> doesn't
/// reach: the empty-selection short-circuit, and a retry that actually WAITS — every retry test in
/// the primary suite sets <c>PublishRetryBaseDelayMs = 0</c>, so the pump's own backoff delay has
/// never executed. A redelivery pump decides what gets retried after a stream-integrity repair; a
/// retry loop that never actually backs off would hammer a throttling broker exactly the way the
/// retry was added to stop.
/// </summary>
public class RedeliveryPumpCoverageTests {

  private static RedeliveryEvent _evt(Guid streamId, Guid eventId, long version) => new() {
    EventId = eventId,
    StreamId = streamId,
    Version = version,
    CommitSequence = version,
    EventType = "Contracts.CoverageProbe",
    EventData = /*lang=json,strict*/ "{\"seeded\":true}",
    Metadata = "{}",
    Scope = null,
    Flags = 0,
  };

  private sealed class _captureSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var payloadType = envelope.Payload!.GetType();
      return new SerializedEnvelope(
        new MessageEnvelope<System.Text.Json.JsonElement> {
          MessageId = envelope.MessageId,
          Payload = default,
          Hops = [.. envelope.Hops],
          DispatchContext = envelope.DispatchContext,
          Target = envelope.Target,
          StateOnly = envelope.StateOnly,
        },
        $"Whizbang.Core.Observability.MessageEnvelope`1[[{payloadType.AssemblyQualifiedName}]], Whizbang.Core",
        payloadType.AssemblyQualifiedName!);
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed class _captureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class _flakyTransport : ITransport {
    public int FailFirst { get; set; }
    public int Attempts { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      Attempts++;
      if (Attempts <= FailFirst) {
        throw new TimeoutException("simulated broker throttle");
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  /// <summary>What breaks: an empty selection means the requester's window found nothing to
  /// repair — publishing zero composites (not one empty one) is the correct no-op.</summary>
  [Test]
  public async Task PublishAsync_EmptySelection_ReturnsZeroWithoutPublishingAsync() {
    var transport = new _captureTransport();
    var pump = new RedeliveryPump(transport, new _captureSerializer(), new ServiceInstanceProvider());

    var published = await pump.PublishAsync([], topic: "repair-topic", target: null);

    await Assert.That(published).IsEqualTo(0);
    await Assert.That(transport.Published).IsEmpty();
  }

  /// <summary>What breaks: a configured backoff must still let a transient failure retry and
  /// succeed — the delay is the whole point of the retry (giving a throttled broker room to
  /// recover), not an obstacle standing between the pump and success.</summary>
  [Test]
  [Timeout(30000)]
  public async Task PublishAsync_TransientFailureWithConfiguredBackoff_ActuallyDelaysBeforeRetryingAsync(CancellationToken testToken) {
    var transport = new _flakyTransport { FailFirst = 1 };
    var pump = new RedeliveryPump(transport, new _captureSerializer(), new ServiceInstanceProvider(),
      options: new RedeliveryPumpOptions { PublishRetryAttempts = 3, PublishRetryBaseDelayMs = 5 });

    var published = await pump.PublishAsync(
      [_evt(TrackedGuid.NewMedo().Value, TrackedGuid.NewMedo().Value, 1)],
      topic: "repair-topic", target: "svc-x", cancellationToken: testToken);

    await Assert.That(published).IsEqualTo(1)
      .Because("a configured backoff must still let the retry succeed");
    await Assert.That(transport.Attempts).IsEqualTo(2)
      .Because("one transient failure, one backoff wait, one successful retry");
  }
}
