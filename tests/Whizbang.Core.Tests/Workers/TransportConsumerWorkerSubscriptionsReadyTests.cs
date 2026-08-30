using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006
#pragma warning disable CS0067

/// <summary>
/// Tests the public <see cref="TransportConsumerWorker.SubscriptionsReady"/>
/// API added to fix the RabbitMQ start-vs-subscribe race that was eating
/// the first messages on every CI run before PR 219. Production callers
/// (readiness probes, downstream-dispatch gating) consume this task to
/// know the consumer is actually receiving — tests use it to eliminate
/// the timing window that drops the first dispatched messages.
/// </summary>
/// <docs>messaging/transports/transport-consumer-readiness</docs>
public class TransportConsumerWorkerSubscriptionsReadyTests {

  private static TransportConsumerWorker _newWorker(ITransport transport, TransportConsumerOptions? options = null) {
    options ??= new TransportConsumerOptions();
    if (options.Destinations.Count == 0) {
      options.Destinations.Add(new TransportDestination("test-topic"));
    }
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => new NoOpStrategy());
    services.AddScoped<IWorkCoordinator>(_ => new Whizbang.Core.Tests.Workers.NoOpWorkCoordinator());
    var sp = services.BuildServiceProvider();
    return new TransportConsumerWorker(
      transport,
      options,
      new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider());
  }

  [Test]
  public async Task SubscriptionsReady_BeforeStartAsync_IsNotCompletedAsync() {
    var worker = _newWorker(new _FakeTransport());

    await Assert.That(worker.SubscriptionsReady.IsCompleted).IsFalse()
      .Because("the readiness signal must NOT be pre-completed — consumers gate startup on this");
  }

  [Test]
  public async Task SubscriptionsReady_CompletesAfterSubscribeReturnsAsync() {
    var transport = new _FakeTransport();
    var worker = _newWorker(transport);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // SubscriptionsReady must complete once _subscribeToAllDestinationsAsync
    // returns — this is the contract that lets test fixtures dispatch
    // without the message landing on an unbound queue.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(worker.SubscriptionsReady.IsCompletedSuccessfully).IsTrue();

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch { /* shutdown best-effort */ }
  }

  [Test]
  public async Task WaitForSubscriptionsReadyAsync_HonorsCancellationAsync() {
    var transport = new _FakeTransport { BlockSubscribe = true };
    var worker = _newWorker(transport);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(CancellationToken.None);

    // Subscribe never returns — so WaitForSubscriptionsReadyAsync must surface
    // cancellation rather than hang.
    var waitTask = worker.WaitForSubscriptionsReadyAsync(cts.Token);
    cts.Cancel();
    await Assert.That(async () => await waitTask).Throws<OperationCanceledException>();

    transport.UnblockSubscribe();
    try { await worker.StopAsync(CancellationToken.None); } catch { /* shutdown best-effort */ }
  }

  // --- minimal fakes ---

  private sealed class _FakeTransport : ITransport {
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool BlockSubscribe { get; init; }
    public void UnblockSubscribe() => _gate.TrySetResult();

    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
      => Task.FromResult<ISubscription>(new _FakeSubscription());

    public async Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      if (BlockSubscribe) {
        await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
      }
      return new _FakeSubscription();
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotSupportedException();
  }

  private sealed class _FakeSubscription : ISubscription {
    public bool IsActive => true;
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() { }
  }

  private sealed class NoOpStrategy : IWorkCoordinatorStrategy {
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default)
      => Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }
}
