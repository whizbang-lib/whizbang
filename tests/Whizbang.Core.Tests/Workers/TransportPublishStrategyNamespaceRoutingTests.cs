using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tags;
using Whizbang.Core.Tests.Tags;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for the TransportNamespace seam on <see cref="TransportPublishStrategy"/> (topology
/// arc phase 8, #424 increment 2): a message whose type resolves to a non-default
/// TransportNamespace gets the key stamped onto destination METADATA — the transport maps the
/// key to a client; the routing strategies stay TransportNamespace-unaware, so Address and
/// RoutingKey never change. With no resolver or no bindings the destination is byte-identical
/// to today's (the spec's single-namespace no-op guarantee).
/// </summary>
[Category("Core")]
[Category("Transports")]
public class TransportPublishStrategyNamespaceRoutingTests {
  internal sealed record BulkClassEvent : IEvent;

  internal sealed record PlainEvent : IEvent;

  [Test]
  public async Task PublishAsync_RoutedType_StampsNamespaceKeyOnMetadataAsync() {
    var transport = new RecordingTransport();
    var strategy = _strategy(transport, _bulkResolver());
    var work = _eventWork(typeof(BulkClassEvent), destination: "myapp.records");

    var result = await strategy.PublishAsync(work, CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    var destination = transport.Published.Single().Destination;
    await Assert.That(TransportNamespaces.FromMetadata(destination.Metadata)).IsEqualTo("bulk");
  }

  [Test]
  public async Task PublishAsync_RoutedType_EntityNameIsUnchangedAsync() {
    // Naming forward-compat lock (plan resolution 5): the strategy names the ENTITY; the
    // TransportNamespace is a metadata post-process. Same Address, same RoutingKey — the
    // class prefix is the NAMESPACE, not the entity name.
    var routedTransport = new RecordingTransport();
    var unroutedTransport = new RecordingTransport();
    var routed = _strategy(routedTransport, _bulkResolver());
    var unrouted = _strategy(unroutedTransport, transportNamespaces: null);

    await routed.PublishAsync(_eventWork(typeof(BulkClassEvent), "myapp.records"), CancellationToken.None);
    await unrouted.PublishAsync(_eventWork(typeof(BulkClassEvent), "myapp.records"), CancellationToken.None);

    var routedDestination = routedTransport.Published.Single().Destination;
    var unroutedDestination = unroutedTransport.Published.Single().Destination;
    await Assert.That(routedDestination.Address).IsEqualTo(unroutedDestination.Address);
    await Assert.That(routedDestination.RoutingKey).IsEqualTo(unroutedDestination.RoutingKey);
  }

  [Test]
  public async Task PublishAsync_UnroutedType_DoesNotStampMetadataAsync() {
    // Unmatched traffic uses default — and stays byte-identical to today's wire shape (no
    // extra metadata → no extra broker application property).
    var transport = new RecordingTransport();
    var strategy = _strategy(transport, _bulkResolver());
    var work = _eventWork(typeof(PlainEvent), destination: "myapp.records");

    await strategy.PublishAsync(work, CancellationToken.None);

    var destination = transport.Published.Single().Destination;
    await Assert.That(destination.Metadata?.ContainsKey(TransportNamespaces.MetadataKey) == true).IsFalse();
  }

  [Test]
  public async Task PublishAsync_NoBindings_DestinationIdenticalToNoResolverAsync() {
    // Single-namespace no-op guarantee: a resolver WITHOUT routing bindings must be
    // indistinguishable from no resolver at all.
    var withResolverTransport = new RecordingTransport();
    var withoutResolverTransport = new RecordingTransport();
    var emptyResolver = new TransportNamespaceResolver(new TagOptions(), () => []);
    var withResolver = _strategy(withResolverTransport, emptyResolver);
    var withoutResolver = _strategy(withoutResolverTransport, transportNamespaces: null);

    await withResolver.PublishAsync(_eventWork(typeof(BulkClassEvent), "myapp.records"), CancellationToken.None);
    await withoutResolver.PublishAsync(_eventWork(typeof(BulkClassEvent), "myapp.records"), CancellationToken.None);

    var a = withResolverTransport.Published.Single().Destination;
    var b = withoutResolverTransport.Published.Single().Destination;
    await Assert.That(a.Address).IsEqualTo(b.Address);
    await Assert.That(a.RoutingKey).IsEqualTo(b.RoutingKey);
    await Assert.That(a.Metadata?.ContainsKey(TransportNamespaces.MetadataKey) == true).IsFalse();
  }

  [Test]
  public async Task PublishBatchAsync_MixedClasses_SplitsGroupsByNamespaceKeyAsync() {
    // Batch groups must never mix TransportNamespaces: each group lands on ONE namespace's
    // client, so a routed item and an unrouted item to the same address make two groups.
    var transport = new RecordingBulkTransport();
    var strategy = _strategy(transport, _bulkResolver());
    var streamId = Guid.CreateVersion7();
    var routed = _eventWork(typeof(BulkClassEvent), "myapp.records", streamId);
    var unrouted = _eventWork(typeof(PlainEvent), "myapp.records", streamId);

    var results = await strategy.PublishBatchAsync([routed, unrouted], CancellationToken.None);

    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(transport.PublishBatchCalls.Count).IsEqualTo(2)
      .Because("same address + same stream but different TransportNamespace keys must split");
    var keys = transport.PublishBatchCalls
      .Select(c => TransportNamespaces.FromMetadata(c.Destination.Metadata))
      .OrderBy(k => k, StringComparer.Ordinal)
      .ToList();
    await Assert.That(keys).IsEquivalentTo(new[] { "bulk", TransportNamespaces.DefaultKey });
  }

  [Test]
  public async Task PublishBatchAsync_SameClass_StaysInOneGroupAsync() {
    var transport = new RecordingBulkTransport();
    var strategy = _strategy(transport, _bulkResolver());
    var streamId = Guid.CreateVersion7();

    var results = await strategy.PublishBatchAsync(
      [_eventWork(typeof(BulkClassEvent), "myapp.records", streamId), _eventWork(typeof(BulkClassEvent), "myapp.records", streamId)],
      CancellationToken.None);

    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(transport.PublishBatchCalls.Count).IsEqualTo(1);
    var destination = transport.PublishBatchCalls.Single().Destination;
    await Assert.That(TransportNamespaces.FromMetadata(destination.Metadata)).IsEqualTo("bulk");
  }

  #region Helpers

  private static TransportNamespaceResolver _bulkResolver() {
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");
    return new TransportNamespaceResolver(
      options, () => [CoalesceGroupResolverTests.TagRegistration(typeof(BulkClassEvent), "bulk-import")]);
  }

  private static TransportPublishStrategy _strategy(
      ITransport transport, TransportNamespaceResolver? transportNamespaces) {
    return new TransportPublishStrategy(
      transport,
      new DefaultTransportReadinessCheck(),
      "inbox",
      loggerFactory: null,
      throttleRetryOptions: null,
      metrics: null,
      postSerializeHookChain: null,
      jsonOptions: null,
      namespaceRouting: null,
      transportNamespaces: transportNamespaces);
  }

  private static OutboxWork _eventWork(Type messageType, string destination, Guid? streamId = null) {
    var messageId = Guid.CreateVersion7();
    return new OutboxWork {
      MessageId = messageId,
      Destination = destination,
      Envelope = new MessageEnvelope<JsonElement>(
        messageId: MessageId.From(messageId),
        payload: JsonDocument.Parse("{}").RootElement,
        hops: []),
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{messageType.AssemblyQualifiedName}]], Whizbang.Core",
      MessageType = messageType.AssemblyQualifiedName!,
      StreamId = streamId ?? Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };
  }

  private sealed class RecordingTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination)> Published { get; } = [];

    public bool IsInitialized => true;

    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) {
      Published.Add((envelope, destination));
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class RecordingBulkTransport : ITransport {
    public List<(IReadOnlyList<BulkPublishItem> Items, TransportDestination Destination)> PublishBatchCalls { get; } = [];

    public bool IsInitialized => true;

    public TransportCapabilities Capabilities =>
      TransportCapabilities.PublishSubscribe | TransportCapabilities.BulkPublish;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(
        IReadOnlyList<BulkPublishItem> items,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      PublishBatchCalls.Add((items, destination));
      IReadOnlyList<BulkPublishItemResult> results =
        [.. items.Select(i => new BulkPublishItemResult { MessageId = i.MessageId, Success = true })];
      return Task.FromResult(results);
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  #endregion
}
