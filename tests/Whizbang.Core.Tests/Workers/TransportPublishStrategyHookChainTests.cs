#pragma warning disable CA1707

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the TransportPublishStrategy's post-serialize hook chain integration:
/// the strategy serializes once, runs the chain, stamps whizbang.body-size on
/// destination metadata, validates against the transport ceiling, and hands
/// the result to the transport via the preSerializedBytes hint — no
/// double-serialize, size known pre-flight, oversized messages fail with a
/// clear reason code.
/// </summary>
/// <docs>fundamentals/work-coordinator/transport-publish-strategy</docs>
public class TransportPublishStrategyHookChainTests {

  [Test]
  public async Task PublishAsync_NoHookChain_SkipsPreSerializeFastPathAsync() {
    var transport = new _captureTransport(maxMessageSizeBytes: null);
    var strategy = new TransportPublishStrategy(
      transport: transport,
      readinessCheck: new _alwaysReadyReadinessCheck(),
      inboxTopic: "test-inbox",
      postSerializeHookChain: null,                          // no chain
      jsonOptions: null);

    var result = await strategy.PublishAsync(_buildWork(), CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPreSerializedBytes).IsNull()
      .Because("No chain + no jsonOptions → strategy skips serialization entirely; transport publishes from the live envelope object. Preserves the existing fast path.");
  }

  [Test]
  public async Task PublishAsync_EmptyChainAndNoTransportCeiling_SkipsPreSerializeAsync() {
    var transport = new _captureTransport(maxMessageSizeBytes: null);
    var strategy = new TransportPublishStrategy(
      transport: transport,
      readinessCheck: new _alwaysReadyReadinessCheck(),
      inboxTopic: "test-inbox",
      postSerializeHookChain: new PostSerializeHookChain([]),    // empty
      jsonOptions: _buildJsonOptions());

    var result = await strategy.PublishAsync(_buildWork(), CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPreSerializedBytes).IsNull()
      .Because("Empty chain + null transport ceiling → nothing needs the size; strategy skips the serialize-for-measurement step.");
  }

  [Test]
  public async Task PublishAsync_TransportHasCeiling_AlwaysSerializesAndStampsBodySizeAsync() {
    var transport = new _captureTransport(maxMessageSizeBytes: 256 * 1024);
    var strategy = new TransportPublishStrategy(
      transport: transport,
      readinessCheck: new _alwaysReadyReadinessCheck(),
      inboxTopic: "test-inbox",
      postSerializeHookChain: new PostSerializeHookChain([]),
      jsonOptions: _buildJsonOptions());

    var result = await strategy.PublishAsync(_buildWork(), CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPreSerializedBytes).IsNotNull()
      .Because("Transport reports a ceiling — strategy MUST serialize to know the size and validate pre-flight.");
    await Assert.That(transport.LastDestination!.Metadata).IsNotNull();
    await Assert.That(transport.LastDestination!.Metadata!.ContainsKey(TransportPublishStrategy.BODY_SIZE_METADATA_KEY)).IsTrue();
    var size = transport.LastDestination.Metadata[TransportPublishStrategy.BODY_SIZE_METADATA_KEY].GetInt32();
    await Assert.That(size).IsEqualTo(transport.LastPreSerializedBytes!.Value.Length)
      .Because("whizbang.body-size MUST match the actual bytes the transport receives — observability tools and future hooks rely on this invariant.");
  }

  [Test]
  public async Task PublishAsync_OversizedAndNoOffload_FailsWithMessageBodyTooLargeAsync() {
    // Set the transport ceiling tiny so the envelope's serialized form exceeds it.
    var transport = new _captureTransport(maxMessageSizeBytes: 10);
    var strategy = new TransportPublishStrategy(
      transport: transport,
      readinessCheck: new _alwaysReadyReadinessCheck(),
      inboxTopic: "test-inbox",
      postSerializeHookChain: new PostSerializeHookChain([]),
      jsonOptions: _buildJsonOptions());

    var result = await strategy.PublishAsync(_buildWork(), CancellationToken.None);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Reason).IsEqualTo(MessageFailureReason.MessageBodyTooLarge)
      .Because("Pre-flight: size > ceiling AND no hook substituted the body — fail with a typed reason code so dashboards distinguish 'too big' from generic transport failures.");
    await Assert.That(result.Error).IsNotNull();
    await Assert.That(result.Error!).Contains("AddWhizbangBodyOffload")
      .Because("Error message points operators at the remediation knob (register offload, raise tier, or trim payload).");
    await Assert.That(transport.PublishCallCount).IsEqualTo(0)
      .Because("Strategy MUST NOT hand oversized payloads to the transport — that's what we're protecting against.");
  }

  [Test]
  public async Task PublishAsync_HookReplacesBody_TransportReceivesReplacementAndUpdatedSizeAsync() {
    var transport = new _captureTransport(maxMessageSizeBytes: null);
    var replacementBytes = "REPLACED_BY_HOOK"u8.ToArray();
    var chain = new PostSerializeHookChain([
      new _substituteHook(order: 1000, replacement: replacementBytes)
    ]);
    var strategy = new TransportPublishStrategy(
      transport: transport,
      readinessCheck: new _alwaysReadyReadinessCheck(),
      inboxTopic: "test-inbox",
      postSerializeHookChain: chain,
      jsonOptions: _buildJsonOptions());

    var result = await strategy.PublishAsync(_buildWork(), CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastPreSerializedBytes!.Value.ToArray()).IsEquivalentTo(replacementBytes)
      .Because("Hook chain replacement MUST flow through to the transport's preSerializedBytes — this is the whole point of the chain (e.g., body-offload claim envelope substitution).");
    await Assert.That(transport.LastDestination!.Metadata![TransportPublishStrategy.BODY_SIZE_METADATA_KEY].GetInt32()).IsEqualTo(replacementBytes.Length)
      .Because("whizbang.body-size reflects the FINAL on-wire bytes (post-substitution), not the original — observability stays accurate.");
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static System.Text.Json.JsonSerializerOptions _buildJsonOptions() =>
    new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

  private static OutboxWork _buildWork() {
    var envelope = new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{\"x\":\"hello\"}").RootElement,
      Hops = [
        new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }
      ]
    };
    return new OutboxWork {
      MessageId = Guid.NewGuid(),
      MessageType = "MyApp.Events.SomeEvent, MyApp",
      Destination = "test-topic",
      EnvelopeType = envelope.GetType().AssemblyQualifiedName!,
      Envelope = envelope,
      Status = MessageProcessingStatus.Stored,
      Attempts = 0,
    };
  }

  private sealed class _captureTransport : ITransport {
    public _captureTransport(long? maxMessageSizeBytes) {
      MaxMessageSizeBytes = maxMessageSizeBytes;
    }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public long? MaxMessageSizeBytes { get; }
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public int PublishCallCount { get; private set; }
    public ReadOnlyMemory<byte>? LastPreSerializedBytes { get; private set; }
    public TransportDestination? LastDestination { get; private set; }
    public IMessageEnvelope? LastEnvelope { get; private set; }

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) {
      PublishCallCount++;
      LastEnvelope = envelope;
      LastDestination = destination;
      LastPreSerializedBytes = preSerializedBytes;
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default)
          => throw new NotImplementedException();

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
          => throw new NotImplementedException();
  }

  private sealed class _alwaysReadyReadinessCheck : ITransportReadinessCheck {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
  }

  private sealed class _substituteHook : IPostSerializeHook {
    private readonly byte[] _replacement;
    public _substituteHook(int order, byte[] replacement) {
      Order = order;
      _replacement = replacement;
    }
    public int Order { get; }
    public Task<PostSerializeResult> RunAsync(PostSerializeContext context, CancellationToken cancellationToken) {
      return Task.FromResult(new PostSerializeResult {
        NewSerializedBytes = _replacement,
      });
    }
  }
}
