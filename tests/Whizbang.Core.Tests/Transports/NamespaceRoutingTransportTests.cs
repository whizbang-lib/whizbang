using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tests for <see cref="NamespaceRoutingTransport"/> — the transport-boundary composition that
/// turns a set of per-TransportNamespace broker clients into one <see cref="ITransport"/>
/// (topology arc phase 8, #424 increment 2). Publish routes by the key the publish strategy
/// stamped on destination metadata; the consume side subscribes its normal set in
/// <c>default</c> PLUS the same entity set in every non-default namespace an active
/// consumed/handled type resolves to. Entity NAMES never change across namespaces — a class
/// prefix is the namespace, not the entity name.
/// </summary>
[Category("Core")]
[Category("Transports")]
public class NamespaceRoutingTransportTests {
  #region Publish routing

  [Test]
  public async Task PublishAsync_StampedKey_RoutesToThatNamespacesTransportAsync() {
    var @default = new RecordingTransport();
    var bulk = new RecordingTransport();
    var transport = _routing(@default, ("bulk", bulk));

    await transport.PublishAsync(
      _envelope(), TransportNamespaces.Stamp(new TransportDestination("myapp.records"), "bulk"));

    await Assert.That(bulk.Published.Count).IsEqualTo(1);
    await Assert.That(@default.Published.Count).IsEqualTo(0);
  }

  [Test]
  public async Task PublishAsync_UnstampedDestination_RoutesToDefaultAsync() {
    var @default = new RecordingTransport();
    var bulk = new RecordingTransport();
    var transport = _routing(@default, ("bulk", bulk));

    await transport.PublishAsync(_envelope(), new TransportDestination("myapp.records"));

    await Assert.That(@default.Published.Count).IsEqualTo(1);
    await Assert.That(bulk.Published.Count).IsEqualTo(0);
  }

  [Test]
  public async Task PublishAsync_KeyWithNoConfiguredNamespace_FallsBackToDefaultAsync() {
    // Graceful degradation: routing bindings without a matching multi-namespace deployment
    // change nothing but a metadata stamp — never a dropped or unroutable message.
    var @default = new RecordingTransport();
    var transport = _routing(@default, ("bulk", new RecordingTransport()));

    await transport.PublishAsync(
      _envelope(), TransportNamespaces.Stamp(new TransportDestination("myapp.records"), "archive"));

    await Assert.That(@default.Published.Count).IsEqualTo(1);
  }

  [Test]
  public async Task PublishAsync_EntityNameIsIdenticalAcrossNamespacesAsync() {
    // The naming invariant: a TransportNamespace changes WHICH BROKER, never the entity name.
    var @default = new RecordingTransport();
    var bulk = new RecordingTransport();
    var transport = _routing(@default, ("bulk", bulk));
    var entity = new TransportDestination("inbox.myapp.orders.commands", "myapp.orders.commands.createorder");

    await transport.PublishAsync(_envelope(), entity);
    await transport.PublishAsync(_envelope(), TransportNamespaces.Stamp(entity, "bulk"));

    await Assert.That(bulk.Published.Single().Destination.Address)
      .IsEqualTo(@default.Published.Single().Destination.Address);
    await Assert.That(bulk.Published.Single().Destination.RoutingKey)
      .IsEqualTo(@default.Published.Single().Destination.RoutingKey);
  }

  [Test]
  public async Task PublishBatchAsync_StampedKey_RoutesToThatNamespacesTransportAsync() {
    var @default = new RecordingTransport();
    var bulk = new RecordingTransport();
    var transport = _routing(@default, ("bulk", bulk));

    await transport.PublishBatchAsync(
      [new BulkPublishItem {
        Envelope = _envelope(),
        EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
        MessageId = Guid.CreateVersion7()
      }],
      TransportNamespaces.Stamp(new TransportDestination("myapp.records"), "bulk"));

    await Assert.That(bulk.BatchCalls.Count).IsEqualTo(1);
    await Assert.That(@default.BatchCalls.Count).IsEqualTo(0);
  }

  #endregion

  #region Consume mirroring

  [Test]
  public async Task SubscribeBatchAsync_ActiveConsumeNamespace_MirrorsTheSameEntityAsync() {
    var @default = new RecordingTransport();
    var bulk = new RecordingTransport();
    var transport = _routing(@default, [("bulk", bulk)], consumeKeys: ["bulk"]);

    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, new TransportDestination("inbox.myapp.orders.commands"), new TransportBatchOptions());

    await Assert.That(@default.Subscribed.Count).IsEqualTo(1);
    await Assert.That(bulk.Subscribed.Count).IsEqualTo(1)
      .Because("the normal set in default PLUS the same entity set in each active class namespace");
    await Assert.That(bulk.Subscribed.Single().Address).IsEqualTo(@default.Subscribed.Single().Address);
    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task SubscribeBatchAsync_NoActiveConsumeNamespaces_SubscribesDefaultOnlyAsync() {
    // Publish-only classes never mint consume-side entities: a namespace nothing HANDLES is
    // never subscribed, so it costs zero broker entities and zero acceptor slots.
    var @default = new RecordingTransport();
    var bulk = new RecordingTransport();
    var transport = _routing(@default, ("bulk", bulk));

    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, new TransportDestination("inbox.myapp.orders.commands"), new TransportBatchOptions());

    await Assert.That(@default.Subscribed.Count).IsEqualTo(1);
    await Assert.That(bulk.Subscribed.Count).IsEqualTo(0);
    await Assert.That(subscription).IsSameReferenceAs(@default.Subscriptions.Single())
      .Because("with nothing to mirror the caller gets the default transport's own subscription — no wrapper");
  }

  [Test]
  public async Task SubscribeBatchAsync_UnconfiguredConsumeKey_IsIgnoredAsync() {
    var @default = new RecordingTransport();
    var bulk = new RecordingTransport();
    var transport = _routing(@default, [("bulk", bulk)], consumeKeys: ["archive"]);

    _ = await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, new TransportDestination("inbox.myapp.orders.commands"), new TransportBatchOptions());

    await Assert.That(bulk.Subscribed.Count).IsEqualTo(0);
    await Assert.That(@default.Subscribed.Count).IsEqualTo(1);
  }

  [Test]
  public async Task CompositeSubscription_PauseResumeDispose_FanOutToEveryNamespaceAsync() {
    var @default = new RecordingTransport();
    var bulk = new RecordingTransport();
    var transport = _routing(@default, [("bulk", bulk)], consumeKeys: ["bulk"]);
    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, new TransportDestination("inbox.myapp.orders.commands"), new TransportBatchOptions());

    await subscription.PauseAsync();
    await Assert.That(subscription.IsActive).IsFalse();
    await Assert.That(bulk.Subscriptions.Single().IsActive).IsFalse();

    await subscription.ResumeAsync();
    await Assert.That(subscription.IsActive).IsTrue();

    subscription.Dispose();
    await Assert.That(@default.Subscriptions.Single().DisposeCount).IsEqualTo(1);
    await Assert.That(bulk.Subscriptions.Single().DisposeCount).IsEqualTo(1);
  }

  #endregion

  #region Lifecycle + capability projection

  [Test]
  public async Task InitializeAsync_InitializesEveryNamespaceAsync() {
    var @default = new RecordingTransport();
    var bulk = new RecordingTransport();
    var transport = _routing(@default, ("bulk", bulk));

    await transport.InitializeAsync();

    await Assert.That(@default.InitializeCount).IsEqualTo(1);
    await Assert.That(bulk.InitializeCount).IsEqualTo(1);
    await Assert.That(transport.IsInitialized).IsTrue();
  }

  [Test]
  public async Task Capabilities_ProjectTheDefaultNamespaceAsync() {
    // Every namespace runs the same broker product with the same options — the default's
    // capabilities and wire ceiling ARE the fleet's, and the publish strategy reads them
    // before any namespace key exists.
    var @default = new RecordingTransport { MaxSizeBytes = 262144 };
    var transport = _routing(@default, ("bulk", new RecordingTransport { MaxSizeBytes = 1024 }));

    await Assert.That(transport.Capabilities).IsEqualTo(@default.Capabilities);
    await Assert.That(transport.MaxMessageSizeBytes).IsEqualTo(262144);
  }

  [Test]
  public async Task DisposeAsync_DisposesEveryNamespaceTransportAsync() {
    var @default = new RecordingTransport();
    var bulk = new RecordingTransport();
    var transport = _routing(@default, ("bulk", bulk));

    await transport.DisposeAsync();

    await Assert.That(@default.DisposeCount).IsEqualTo(1);
    await Assert.That(bulk.DisposeCount).IsEqualTo(1);
  }

  [Test]
  public async Task SetRecoveryHandler_FansOutToEveryRecoveryAwareNamespaceAsync() {
    var @default = new RecordingTransport();
    var bulk = new RecordingTransport();
    var transport = _routing(@default, ("bulk", bulk));

    ((ITransportWithRecovery)transport).SetRecoveryHandler(_ => Task.CompletedTask);

    await Assert.That(@default.RecoveryHandler).IsNotNull();
    await Assert.That(bulk.RecoveryHandler).IsNotNull();
  }

  [Test]
  public async Task NamespaceKeys_ListDefaultFirstThenOrdinalAsync() {
    var transport = _routing(
      new RecordingTransport(), ("control", new RecordingTransport()), ("bulk", new RecordingTransport()));

    await Assert.That(transport.NamespaceKeys)
      .IsEquivalentTo(new[] { TransportNamespaces.DefaultKey, "bulk", "control" });
  }

  [Test]
  public async Task Constructor_NullDefaultTransport_ThrowsAsync() {
    await Assert.That(() => new NamespaceRoutingTransport(null!, new Dictionary<string, ITransport>()))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_DefaultKeyInTheNonDefaultMap_ThrowsAsync() {
    // 'default' is the constructor's first parameter — carrying it in the peer map too would
    // silently create a second client for the same namespace.
    var peers = new Dictionary<string, ITransport> { [TransportNamespaces.DefaultKey] = new RecordingTransport() };

    await Assert.That(() => new NamespaceRoutingTransport(new RecordingTransport(), peers))
      .Throws<ArgumentException>();
  }

  #endregion

  #region Helpers

  private static NamespaceRoutingTransport _routing(
      ITransport @default, params (string Key, ITransport Transport)[] peers) =>
    _routing(@default, peers, consumeKeys: null);

  private static NamespaceRoutingTransport _routing(
      ITransport @default,
      IReadOnlyList<(string Key, ITransport Transport)> peers,
      IReadOnlyList<string>? consumeKeys) =>
    new(@default, peers.ToDictionary(p => p.Key, p => p.Transport, StringComparer.Ordinal),
      consumeKeys is null ? null : () => consumeKeys);

  private static MessageEnvelope<JsonElement> _envelope() {
    var id = Guid.CreateVersion7();
    return new MessageEnvelope<JsonElement>(
      messageId: MessageId.From(id), payload: JsonDocument.Parse("{}").RootElement, hops: []);
  }

  private sealed class RecordingTransport : ITransport, ITransportWithRecovery, IAsyncDisposable {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination)> Published { get; } = [];
    public List<TransportDestination> BatchCalls { get; } = [];
    public List<TransportDestination> Subscribed { get; } = [];
    public List<RecordingSubscription> Subscriptions { get; } = [];
    public int InitializeCount { get; private set; }
    public int DisposeCount { get; private set; }
    public long? MaxSizeBytes { get; init; }
    public Func<CancellationToken, Task>? RecoveryHandler { get; private set; }

    public bool IsInitialized => InitializeCount > 0;

    public TransportCapabilities Capabilities =>
      TransportCapabilities.PublishSubscribe | TransportCapabilities.BulkPublish;

    public long? MaxMessageSizeBytes => MaxSizeBytes;

    public Task InitializeAsync(CancellationToken cancellationToken = default) {
      InitializeCount++;
      return Task.CompletedTask;
    }

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) {
      Published.Add((envelope, destination));
      return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(
        IReadOnlyList<BulkPublishItem> items,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      BatchCalls.Add(destination);
      IReadOnlyList<BulkPublishItemResult> results =
        [.. items.Select(i => new BulkPublishItemResult { MessageId = i.MessageId, Success = true })];
      return Task.FromResult(results);
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      Subscribed.Add(destination);
      var subscription = new RecordingSubscription();
      Subscriptions.Add(subscription);
      return Task.FromResult<ISubscription>(subscription);
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();

    public void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered) => RecoveryHandler = onRecovered;

    public ValueTask DisposeAsync() {
      DisposeCount++;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingSubscription : ISubscription {
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public bool IsActive { get; private set; } = true;

    public int DisposeCount { get; private set; }

    public Task PauseAsync() {
      IsActive = false;
      return Task.CompletedTask;
    }

    public Task ResumeAsync() {
      IsActive = true;
      return Task.CompletedTask;
    }

    public void Dispose() {
      DisposeCount++;
      OnDisconnected?.Invoke(this, new SubscriptionDisconnectedEventArgs());
    }
  }

  #endregion
}
