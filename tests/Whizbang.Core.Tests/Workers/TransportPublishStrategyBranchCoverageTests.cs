// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branch coverage for <see cref="TransportPublishStrategy"/>: routing keys for commands and events whose
/// type name carries no namespace, the bulk path's decision whether to pre-serialize (a hook chain without
/// serializer options, an empty chain against a transport with no ceiling, a non-empty chain against one),
/// a bulk result that failed for a reason other than throttling, and the throttle counter on the bulk path.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportPublishStrategy.cs</code-under-test>
[Category("Workers")]
public class TransportPublishStrategyBranchCoverageTests {

  private static ThrottleRetryOptions _fastOpts(int maxAttempts = 5) => new() {
    MaxAttempts = maxAttempts,
    BaseDelay = TimeSpan.FromMilliseconds(1),
    BackoffMultiplier = 1.0,
    MaxDelay = TimeSpan.FromMilliseconds(5),
  };

  // ============================================================
  // ResolveEntityDestination: type names with no namespace
  // ============================================================

  [Test]
  public async Task ResolveEntityDestination_CommandWithoutNamespace_RoutesToInboxKeyedByBareTypeNameAsync() {
    var strategy = _strategy(new BulkCaptureTransport(maxMessageSizeBytes: null));
    var work = _work() with { MessageType = "CreateOrderCommand, BranchAssembly", Destination = "ignored-topic" };

    var destination = strategy.ResolveEntityDestination(work);

    await Assert.That(destination.Address).IsEqualTo("branch-inbox")
      .Because("a command always rides the inbox topic, whatever destination the row carried");
    await Assert.That(destination.RoutingKey).IsEqualTo("createordercommand")
      .Because("with no namespace the routing key is the lowercased type name alone, never a leading dot");
  }

  [Test]
  public async Task ResolveEntityDestination_CommandWithNamespace_PrefixesTheNamespaceAsync() {
    var strategy = _strategy(new BulkCaptureTransport(maxMessageSizeBytes: null));
    var work = _work() with { MessageType = "Branch.Orders.CreateOrderCommand, BranchAssembly" };

    var destination = strategy.ResolveEntityDestination(work);

    await Assert.That(destination.Address).IsEqualTo("branch-inbox");
    await Assert.That(destination.RoutingKey).IsEqualTo("branch.orders.createordercommand");
  }

  [Test]
  public async Task ResolveEntityDestination_RowWithNoMessageType_PublishesToItsDestinationWithAnEmptyKeyAsync() {
    // A row whose type name is missing classifies as neither command nor event; it is published to the
    // destination it carries, keyed by nothing rather than failing on a name it does not have.
    var strategy = _strategy(new BulkCaptureTransport(maxMessageSizeBytes: null));
    var work = _work() with { MessageType = null!, Destination = "orders-topic" };

    var destination = strategy.ResolveEntityDestination(work);

    await Assert.That(destination.Address).IsEqualTo("orders-topic");
    await Assert.That(destination.RoutingKey).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task ResolveEntityDestination_EventWithoutNamespace_KeysByBareTypeNameAsync() {
    var strategy = _strategy(new BulkCaptureTransport(maxMessageSizeBytes: null));
    var work = _work() with { MessageType = "OrderCreated, BranchAssembly", Destination = "orders-topic" };

    var destination = strategy.ResolveEntityDestination(work);

    await Assert.That(destination.Address).IsEqualTo("orders-topic");
    await Assert.That(destination.RoutingKey).IsEqualTo("ordercreated")
      .Because("a subscription filter on the subject must match the bare name, not '.ordercreated'");
  }

  // ============================================================
  // PublishBatchAsync: whether each item is pre-serialized
  // ============================================================

  [Test]
  public async Task PublishBatchAsync_HookChainWithoutJsonOptions_SkipsPreSerializeAsync() {
    var transport = new BulkCaptureTransport(maxMessageSizeBytes: 256 * 1024);
    var strategy = new TransportPublishStrategy(
      transport: transport,
      readinessCheck: new DefaultTransportReadinessCheck(),
      inboxTopic: "branch-inbox",
      loggerFactory: NullLoggerFactory.Instance,
      namespaceRouting: NullCommandInboxAddressResolver.Instance,
      postSerializeHookChain: new PostSerializeHookChain([new MetadataAddingHook("custom-header", "custom-value")]),
      jsonOptions: null);

    var results = await strategy.PublishBatchAsync([_work(), _work()], CancellationToken.None);

    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(transport.LastBulkItems!.All(i => i.PreSerializedBytes is null && i.PerItemMetadata is null)).IsTrue()
      .Because("without serializer options the chain cannot run, even with hooks and a transport ceiling");
  }

  [Test]
  public async Task PublishBatchAsync_EmptyChainAndNoTransportCeiling_SkipsPreSerializeAsync() {
    var transport = new BulkCaptureTransport(maxMessageSizeBytes: null);
    var strategy = new TransportPublishStrategy(
      transport: transport,
      readinessCheck: new DefaultTransportReadinessCheck(),
      inboxTopic: "branch-inbox",
      loggerFactory: NullLoggerFactory.Instance,
      namespaceRouting: NullCommandInboxAddressResolver.Instance,
      postSerializeHookChain: new PostSerializeHookChain([]),
      jsonOptions: _jsonOptions());

    var results = await strategy.PublishBatchAsync([_work(), _work()], CancellationToken.None);

    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(transport.LastBulkItems!.All(i => i.PreSerializedBytes is null)).IsTrue()
      .Because("with no hook to run and no ceiling to check, serializing up front is pure cost");
  }

  [Test]
  public async Task PublishBatchAsync_NonEmptyChainAndNoTransportCeiling_PreSerializesEachItemAsync() {
    var transport = new BulkCaptureTransport(maxMessageSizeBytes: null);
    var strategy = new TransportPublishStrategy(
      transport: transport,
      readinessCheck: new DefaultTransportReadinessCheck(),
      inboxTopic: "branch-inbox",
      loggerFactory: NullLoggerFactory.Instance,
      namespaceRouting: NullCommandInboxAddressResolver.Instance,
      postSerializeHookChain: new PostSerializeHookChain([new MetadataAddingHook("custom-header", "custom-value")]),
      jsonOptions: _jsonOptions());

    var results = await strategy.PublishBatchAsync([_work(), _work()], CancellationToken.None);

    await Assert.That(results.All(r => r.Success)).IsTrue();
    var items = transport.LastBulkItems!;
    await Assert.That(items.Count).IsEqualTo(2);
    await Assert.That(items.All(i => i.PreSerializedBytes is not null)).IsTrue()
      .Because("a registered hook needs the bytes, so each item is serialized even without a ceiling");
    await Assert.That(items.All(i => i.PerItemMetadata?["custom-header"].GetString() == "custom-value")).IsTrue()
      .Because("the hook's metadata reaches every item it ran for");
  }

  // ============================================================
  // PublishBatchAsync: per-item failures and throttling
  // ============================================================

  [Test]
  public async Task PublishBatchAsync_ItemFailsForANonThrottleReason_IsNotRetriedAndKeepsItsStatusAsync() {
    var transport = new BulkCaptureTransport(maxMessageSizeBytes: null) { FailEveryItemWith = "message rejected by broker" };
    var strategy = _strategy(transport);
    var streamId = Guid.CreateVersion7();
    var works = new[] { _work() with { StreamId = streamId }, _work() with { StreamId = streamId } };

    var results = await strategy.PublishBatchAsync(works, CancellationToken.None);

    await Assert.That(transport.BatchCalls).IsEqualTo(1)
      .Because("a batch whose items failed for their own reasons is not a broker pause, so it is not retried");
    await Assert.That(results.Count).IsEqualTo(2);
    await Assert.That(results.All(r => !r.Success && r.Error == "message rejected by broker")).IsTrue();
    await Assert.That(results.All(r => r.CompletedStatus == MessageProcessingStatus.Stored)).IsTrue()
      .Because("a failed publish leaves the row at the status it was stored with");
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task PublishBatchAsync_ThrottledOnceWithMetrics_CountsEveryItemInTheGroupAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var transport = new BulkThrottleOnceTransport();
    var strategy = new TransportPublishStrategy(
      transport: transport,
      readinessCheck: new DefaultTransportReadinessCheck(),
      inboxTopic: "branch-inbox",
      loggerFactory: NullLoggerFactory.Instance,
      namespaceRouting: NullCommandInboxAddressResolver.Instance,
      throttleRetryOptions: _fastOpts(),
      metrics: metrics);
    var streamId = Guid.CreateVersion7();
    var works = new[] { _work() with { StreamId = streamId }, _work() with { StreamId = streamId } };

    var results = await strategy.PublishBatchAsync(works, CancellationToken.None);

    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(transport.BatchCalls).IsEqualTo(2);
    var throttled = helper.GetByName("whizbang.transport.outbox.publish_throttled")
      .Where(m => m.Tags.TryGetValue("transport", out var t) && t == nameof(BulkThrottleOnceTransport))
      .Sum(m => m.Value);
    await Assert.That(throttled).IsEqualTo(2d)
      .Because("a throttled batch counts every message it held back, tagged with the transport that throttled");
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static TransportPublishStrategy _strategy(ITransport transport) =>
    new(
      transport: transport,
      readinessCheck: new DefaultTransportReadinessCheck(),
      inboxTopic: "branch-inbox",
      loggerFactory: NullLoggerFactory.Instance,
      namespaceRouting: NullCommandInboxAddressResolver.Instance,
      throttleRetryOptions: _fastOpts());

  private static JsonSerializerOptions _jsonOptions() =>
    new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

  private static OutboxWork _work() {
    var envelope = new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{\"x\":\"hello\"}").RootElement,
      Hops = [
        new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }
      ]
    };
    return new OutboxWork {
      MessageId = Guid.CreateVersion7(),
      MessageType = "Branch.Events.SomethingHappened, BranchAssembly",
      Destination = "branch-topic",
      EnvelopeType = envelope.GetType().AssemblyQualifiedName!,
      Envelope = envelope,
      Status = MessageProcessingStatus.Stored,
      Attempts = 0,
    };
  }

  // ============================================================
  // Test doubles
  // ============================================================

  /// <summary>Bulk-capable transport that records the last batch and can fail every item with one error.</summary>
  private sealed class BulkCaptureTransport(long? maxMessageSizeBytes) : ITransport {
    private int _batchCalls;

    public string? FailEveryItemWith { get; init; }
    public int BatchCalls => Volatile.Read(ref _batchCalls);
    public IReadOnlyList<BulkPublishItem>? LastBulkItems { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.BulkPublish;
    public long? MaxMessageSizeBytes { get; } = maxMessageSizeBytes;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(
        IReadOnlyList<BulkPublishItem> items, TransportDestination destination,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _batchCalls);
      LastBulkItems = items;
      IReadOnlyList<BulkPublishItemResult> results = [.. items.Select(i => new BulkPublishItemResult {
        MessageId = i.MessageId,
        Success = FailEveryItemWith is null,
        Error = FailEveryItemWith,
      })];
      return Task.FromResult(results);
    }

    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope,
        TransportDestination destination, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  /// <summary>Bulk-capable transport whose first batch call is throttled by the broker; later calls succeed.</summary>
  private sealed class BulkThrottleOnceTransport : ITransport {
    private int _batchCalls;

    public int BatchCalls => Volatile.Read(ref _batchCalls);
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.BulkPublish;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(
        IReadOnlyList<BulkPublishItem> items, TransportDestination destination,
        CancellationToken cancellationToken = default) {
      if (Interlocked.Increment(ref _batchCalls) == 1) {
        throw new Azure.Messaging.ServiceBus.ServiceBusException(
          "batch send terminated; namespace is being throttled. Error code : 50009. (ServiceBusy)");
      }
      return Task.FromResult<IReadOnlyList<BulkPublishItemResult>>(
        [.. items.Select(i => new BulkPublishItemResult { MessageId = i.MessageId, Success = true })]);
    }

    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope,
        TransportDestination destination, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  /// <summary>A post-serialize hook contributing one destination-metadata entry.</summary>
  private sealed class MetadataAddingHook(string key, string value) : IPostSerializeHook {
    public int Order => 100;
    public Task<PostSerializeResult> RunAsync(PostSerializeContext context, CancellationToken cancellationToken) =>
      Task.FromResult(new PostSerializeResult {
        AdditionalDestinationMetadata = new Dictionary<string, JsonElement> {
          [key] = JsonDocument.Parse($"\"{value}\"").RootElement
        }
      });
  }
}
