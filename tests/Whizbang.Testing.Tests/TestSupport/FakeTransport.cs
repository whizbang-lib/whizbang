using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Testing.Tests.TestSupport;

/// <summary>
/// Recording <see cref="ITransport"/> test double. Captures published envelopes and the
/// batch handler from <see cref="SubscribeBatchAsync"/>. When <see cref="LoopbackOnPublish"/>
/// is set, published envelopes are synchronously delivered back through the captured batch
/// handler - giving tests a fully signal-driven publish/receive round trip.
/// </summary>
internal sealed class FakeTransport : ITransport, IAsyncDisposable {
  public List<IMessageEnvelope> Published { get; } = [];
  public Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? BatchHandler { get; private set; }
  public FakeSubscription? Subscription { get; private set; }
  public bool LoopbackOnPublish { get; set; }
  public Action<IMessageEnvelope>? OnPublish { get; set; }
  public bool Disposed { get; private set; }

  public bool IsInitialized => true;

  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  public async Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType = null,
    ReadOnlyMemory<byte>? preSerializedBytes = null,
    CancellationToken cancellationToken = default) {
    Published.Add(envelope);
    OnPublish?.Invoke(envelope);
    if (LoopbackOnPublish && BatchHandler is not null) {
      await BatchHandler([new TransportMessage(envelope, null)], cancellationToken);
    }
  }

  public Task<ISubscription> SubscribeBatchAsync(
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportDestination destination,
    TransportBatchOptions batchOptions,
    CancellationToken cancellationToken = default) {
    BatchHandler = batchHandler;
    Subscription = new FakeSubscription();
    return Task.FromResult<ISubscription>(Subscription);
  }

  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
    IMessageEnvelope requestEnvelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default)
    where TRequest : notnull
    where TResponse : notnull {
    throw new NotSupportedException("Request/response is not supported by this test double.");
  }

  public ValueTask DisposeAsync() {
    Disposed = true;
    return ValueTask.CompletedTask;
  }
}

/// <summary>
/// Recording <see cref="ISubscription"/> test double.
/// </summary>
internal sealed class FakeSubscription : ISubscription {
#pragma warning disable CS0067 // Event is never raised - test double
  public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

  public bool IsActive { get; private set; } = true;
  public bool Disposed { get; private set; }

  public Task PauseAsync() {
    IsActive = false;
    return Task.CompletedTask;
  }

  public Task ResumeAsync() {
    IsActive = true;
    return Task.CompletedTask;
  }

  public void Dispose() {
    Disposed = true;
  }
}
