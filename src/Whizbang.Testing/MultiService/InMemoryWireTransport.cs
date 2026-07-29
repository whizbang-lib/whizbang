using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Testing.MultiService;

/// <summary>
/// A broker-faithful in-memory <see cref="ITransport"/> shared by every service host in a
/// <see cref="MultiServiceHarness"/>. Faithful means the WIRE BOUNDARY is real: a published
/// envelope is serialized to JSON bytes, and each subscriber receives a freshly-deserialized
/// <c>MessageEnvelope&lt;JsonElement&gt;</c> plus the envelope-type string — the same shape a real
/// broker delivers. Cross-service defects that only appear when the payload loses its CLR type at
/// the boundary (flag derivation, TTL metadata, type-name resolution) reproduce here; a fake that
/// hands the publisher's typed envelope straight to the subscriber hides them.
/// </summary>
/// <docs>testing/multi-service-harness</docs>
public sealed class InMemoryWireTransport : ITransport {
  private readonly Lock _lock = new();
  private readonly Dictionary<string, List<Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>>> _subscribers = [];
  private readonly JsonSerializerOptions _wireOptions;

  /// <summary>
  /// Creates the shared wire. <paramref name="wireOptions"/> serializes outbound envelopes and
  /// deserializes inbound ones — pass options whose resolver knows the envelope + payload types
  /// (the harness supplies the combined <c>JsonContextRegistry</c> options by default).
  /// </summary>
  public InMemoryWireTransport(JsonSerializerOptions wireOptions) {
    ArgumentNullException.ThrowIfNull(wireOptions);
    _wireOptions = wireOptions;
  }

  /// <inheritdoc />
  public bool IsInitialized => true;

  /// <inheritdoc />
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;

  /// <inheritdoc />
  public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <inheritdoc />
  public async Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      ReadOnlyMemory<byte>? preSerializedBytes = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(destination);

    // The wire: bytes, never the publisher's object graph.
    byte[] bytes;
    if (preSerializedBytes is { } pre) {
      bytes = pre.ToArray();
    } else {
      var typeInfo = _wireOptions.GetTypeInfo(envelope.GetType())
        ?? throw new InvalidOperationException(
          $"No JsonTypeInfo for envelope type {envelope.GetType().FullName}. " +
          "Register the payload's JsonSerializerContext (JsonContextRegistry) before publishing.");
      bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);
    }

    var wireEnvelopeType = envelopeType
      ?? throw new InvalidOperationException(
        "envelopeType is required on publish — a real broker carries it as message metadata and " +
        "the receive path cannot type the payload without it.");

    List<Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>> handlers;
    lock (_lock) {
      handlers = _subscribers.TryGetValue(destination.Address, out var list) ? [.. list] : [];
    }

    foreach (var handler in handlers) {
      // Each subscriber gets its OWN deserialization — no shared object graph across services.
      var jsonEnvelopeTypeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<MessageEnvelope<JsonElement>>)
        _wireOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
      var received = JsonSerializer.Deserialize(bytes, jsonEnvelopeTypeInfo)
        ?? throw new InvalidOperationException("Wire deserialization produced a null envelope.");
      await handler([new TransportMessage(received, wireEnvelopeType)], cancellationToken);
    }
  }

  /// <inheritdoc />
  public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(batchHandler);
    ArgumentNullException.ThrowIfNull(destination);
    lock (_lock) {
      if (!_subscribers.TryGetValue(destination.Address, out var list)) {
        list = [];
        _subscribers[destination.Address] = list;
      }
      list.Add(batchHandler);
    }
    return Task.FromResult<ISubscription>(new WireSubscription());
  }

  /// <inheritdoc />
  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull
    => throw new NotSupportedException("Request/response is not part of the multi-service wire harness.");

  private sealed class WireSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;

#pragma warning disable CS0067 // Required by the interface; the in-memory wire never disconnects.
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() {
      IsActive = false;
      return Task.CompletedTask;
    }

    public Task ResumeAsync() {
      IsActive = true;
      return Task.CompletedTask;
    }

    public void Dispose() {
      IsActive = false;
    }
  }
}
