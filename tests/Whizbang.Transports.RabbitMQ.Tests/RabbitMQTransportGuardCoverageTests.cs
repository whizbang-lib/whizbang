#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)
#pragma warning disable CS0067 // Event is never used (test double)
#pragma warning disable CA1822 // Member does not access instance data (test double)

using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Covers guard branches on <see cref="RabbitMQTransport"/> that are reachable without a live
/// broker: the consumer-provisioned-entity verification call site on the batch publish path,
/// correlation/causation header propagation, the batch-flush empty-list defensive guard, the
/// successful-batch debug log, the paused-flush per-message exception isolation, the
/// RoutingPattern-present-but-empty fallback, the null-headers defensive read, the
/// JsonValueKind.Undefined metadata-conversion fallback, and idempotent disposal. All broker
/// interaction goes through <see cref="FakeChannel"/>/<see cref="RecordingChannel"/> (see
/// TestDoubles.cs and RabbitMQTransportBatchPathTests.cs) — never a real connection.
/// </summary>
public class RabbitMQTransportGuardCoverageTests {

  // --- PublishBatchAsync: consumer-provisioned-entity verification call site ---

  [Test]
  public async Task PublishBatchAsync_RequiresProvisionedEntity_AlreadyProvisioned_PublishesWithoutActiveDeclareAsync() {
    // Command-inbox exchanges are provisioned by the CONSUMING service, never the publisher —
    // an active declare here would silently create a bindingless exchange that swallows every
    // message dropped into it. This is the "consumer already provisioned it" happy path:
    // passive verification succeeds and the batch publishes normally.
    var channel = new FakeChannel();
    channel.ExistingExchanges.Add("inbox.orders.commands");
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var destination = new TransportDestination(
      "inbox.orders.commands",
      "#",
      new Dictionary<string, JsonElement> {
        [CommandInboxNaming.RequireProvisionedEntityMetadataKey] = JsonDocument.Parse("true").RootElement.Clone()
      });
    var items = new List<BulkPublishItem> {
      new() { Envelope = RabbitTestWire.NewEnvelope("provisioned"), EnvelopeType = null, MessageId = Guid.CreateVersion7() }
    };

    var results = await transport.PublishBatchAsync(items, destination);

    await Assert.That(results.Single().Success).IsTrue();
    await Assert.That(channel.PassiveExchangeDeclareCount).IsEqualTo(1)
      .Because("existence must be verified passively before every batch publish to this entity");
    await Assert.That(channel.ExchangeDeclareCount).IsEqualTo(0)
      .Because("a consumer-provisioned inbox must never be actively created by the publisher");
  }

  // --- Correlation/causation header propagation ---

  [Test]
  public async Task PublishAsync_EnvelopeWithCorrelationAndCausationIds_SetsWireHeadersAsync() {
    // Downstream services stitch distributed traces and causal chains from these two IDs; if
    // the guard that copies them onto the wire regresses, correlation across services silently
    // breaks with no functional test failure to catch it.
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("with-correlation"),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown,
          CorrelationId = correlationId,
          CausationId = causationId
        }
      ]
    };

    await transport.PublishAsync(envelope, RabbitTestWire.Destination());

    await Assert.That(channel.LastPublishedProperties!.CorrelationId).IsEqualTo(correlationId.Value.ToString());
    await Assert.That((string?)channel.LastPublishedProperties.Headers!["CausationId"])
      .IsEqualTo(causationId.Value.ToString());
  }

  // --- _flushBatchAsync: empty-list defensive guard (invoked via reflection — the collector
  // that is the only production caller already guards against calling back with zero pending
  // messages, so this branch is otherwise unreachable without bypassing that guarantee). ---

  [Test]
  public async Task FlushBatchAsync_EmptyPendingMessages_NeverInvokesHandlerAsync() {
    // Defensive guard: if anything ever calls the flush path with zero collected messages (a
    // future collector change, a race), the batch handler must never run — handing an empty
    // batch to application code risks a `.Single()`-style crash or a misleading "processed a
    // batch" log downstream.
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var pendingType = typeof(RabbitMQTransport).GetNestedType("PendingRabbitMessage", BindingFlags.NonPublic)
      ?? throw new InvalidOperationException("PendingRabbitMessage nested type not found - was it renamed?");
    var emptyList = Activator.CreateInstance(typeof(List<>).MakeGenericType(pendingType))!;
    var method = typeof(RabbitMQTransport).GetMethod("_flushBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)
      ?? throw new InvalidOperationException("_flushBatchAsync not found on RabbitMQTransport - was it renamed?");
    var handlerCalled = false;
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> handler = (_, _) => {
      handlerCalled = true;
      return Task.CompletedTask;
    };

    var task = (Task)method.Invoke(transport, [emptyList, handler, null, "some-queue"])!;
    await task;

    await Assert.That(handlerCalled).IsFalse();
  }

  // --- Successful batch flush: debug log ---

  [Test]
  public async Task SubscribeBatchAsync_SuccessfulFlushWithDebugLogger_LogsProcessedBatchSizeAsync() {
    // Operators diagnosing throughput lean on this debug line to see how many messages actually
    // flushed per queue; if the enabled-check or the log call itself regresses, that visibility
    // silently disappears with no functional test failure to catch it.
    var logger = new CapturingLogger<RabbitMQTransport>();
    var processedLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    logger.OnLog = (level, message) => {
      if (level == LogLevel.Debug && message.Contains("Processed batch of", StringComparison.Ordinal)) {
        processedLogged.TrySetResult();
      }
    };
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);
    var options = new TransportBatchOptions { BatchSize = 1, SlideMs = 60_000, MaxWaitMs = 60_000 };

    await transport.SubscribeBatchAsync((batch, ct) => Task.CompletedTask, RabbitTestWire.Destination(), options);
    var (props, body) = RabbitTestWire.ValidWireMessage("solo");
    await RabbitTestWire.DeliverAsync(channel, props, body, 1);

    await processedLogged.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Debug
      && e.Message.Contains("Processed batch of 1 messages", StringComparison.Ordinal))).IsTrue();
  }

  // --- Paused-flush per-message exception isolation ---

  [Test]
  public async Task SubscribeBatchAsync_PausedFlushMessageIdAccessThrows_SkipsThatMessageAndContinuesAsync() {
    // If reading a property off a torn-down channel's message throws while logging the
    // paused-nack warning, that must not abort the whole paused-flush loop — one bad message
    // must never leave the rest of the pending batch neither acked nor nacked (redelivered
    // forever because nobody ever settles it).
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);
    var options = new TransportBatchOptions { BatchSize = 50, SlideMs = 250, MaxWaitMs = 30_000 };
    var batches = new List<IReadOnlyList<TransportMessage>>();

    var subscription = await transport.SubscribeBatchAsync(
      (batch, ct) => { batches.Add(batch); return Task.CompletedTask; },
      RabbitTestWire.Destination(),
      options);

    var (_, poisonedBody) = RabbitTestWire.ValidWireMessage("poisoned");
    await RabbitTestWire.DeliverAsync(channel, new ThrowingMessageIdBasicProperties(), poisonedBody, 1);
    var (healthyProps, healthyBody) = RabbitTestWire.ValidWireMessage("healthy");
    await RabbitTestWire.DeliverAsync(channel, healthyProps, healthyBody, 2);
    await subscription.PauseAsync();

    await channel.WaitForNackAttemptsAsync(1);

    await Assert.That(channel.NackAttempts).Count().IsEqualTo(1)
      .Because("the first message's properties throw before BasicNackAsync is ever reached for it");
    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(2UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsTrue();
    await Assert.That(channel.AckAttempts).IsEmpty();
    await Assert.That(batches).IsEmpty();
  }

  // --- RoutingPattern present but empty: falls through to the comma-split fallback ---

  [Test]
  public async Task SubscribeBatchAsync_EmptyRoutingPatternMetadata_FallsBackToCommaSplitRoutingKeyAsync() {
    // A metadata entry present but empty ("RoutingPattern": "") must not be treated as a real
    // override — falling through to the comma-split RoutingKey fallback is what keeps a
    // misconfigured empty override from silently binding to nothing (or "#", matching
    // everything) instead of the intended multi-pattern set.
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var destination = RabbitTestWire.Destination(
      routingKey: "orders.created,orders.updated",
      extraMetadata: new Dictionary<string, JsonElement> {
        ["RoutingPattern"] = JsonDocument.Parse("\"\"").RootElement.Clone()
      });

    await transport.SubscribeBatchAsync((batch, ct) => Task.CompletedTask, destination, new TransportBatchOptions());

    var boundKeys = channel.QueueBindings.Select(b => b.RoutingKey).ToList();
    await Assert.That(boundKeys).Contains("orders.created");
    await Assert.That(boundKeys).Contains("orders.updated")
      .Because("both comma-separated patterns must be bound, or half the intended traffic never arrives");
    await Assert.That(boundKeys).DoesNotContain("#")
      .Because("collapsing an empty override to match-all is the dangerous failure -- the queue would "
             + "quietly receive every message on the exchange instead of the two it asked for");
  }

  // --- _tryReadStringHeader: null-headers defensive guard (invoked via reflection — the only
  // two production call sites pass a Headers dictionary already proven non-null by an earlier
  // check, so this branch is otherwise unreachable). ---

  [Test]
  public async Task TryReadStringHeader_NullHeaders_ReturnsNullAsync() {
    // Purely defensive: today's callers only reach this after already confirming Headers is
    // non-null. If that guard is ever relaxed, reading any header from a null dictionary must
    // return null rather than throwing a NullReferenceException mid-deserialization.
    var method = typeof(RabbitMQTransport).GetMethod("_tryReadStringHeader", BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException("_tryReadStringHeader not found on RabbitMQTransport - was it renamed?");

    var result = method.Invoke(null, [null, "AnyKey"]);

    await Assert.That(result).IsNull();
  }

  // --- _convertJsonElementToRabbitMqValue: JsonValueKind.Undefined fallback ---

  [Test]
  public async Task PublishAsync_MetadataValueIsUndefinedJsonElement_ThrowsInvalidOperationExceptionAsync() {
    // Destination metadata should always be built from real parsed JSON. If a caller ever hands
    // in a default/uninitialized JsonElement, the conversion must fail loudly during publish
    // rather than writing a garbage header value onto the wire.
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var destination = new TransportDestination(
      "test-exchange",
      "#",
      new Dictionary<string, JsonElement> { ["weird"] = default });

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
      transport.PublishAsync(RabbitTestWire.NewEnvelope("undefined-metadata"), destination));
  }

  // --- DisposeAsync idempotency ---

  [Test]
  public async Task DisposeAsync_CalledTwice_IsIdempotentAsync() {
    // Shutdown code paths (DI container teardown, explicit cleanup, retry after a partial
    // failure) can legitimately call DisposeAsync more than once; the second call must be a
    // safe no-op, never re-run teardown logic or throw.
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    await transport.DisposeAsync();

    Exception? caught = null;
    try {
      await transport.DisposeAsync();
    } catch (Exception ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNull()
      .Because("a second dispose must be a safe no-op, not throw or re-run teardown logic");
  }

  /// <summary>
  /// IReadOnlyBasicProperties whose MessageId getter throws ObjectDisposedException — simulates
  /// reading a delivered message's properties after the channel/connection has been torn down,
  /// exactly the window <c>_nackPausedMessageAsync</c>'s doc comment describes (one last
  /// delivery between subscription pause and channel teardown).
  /// </summary>
  private sealed class ThrowingMessageIdBasicProperties : IReadOnlyBasicProperties {
    public string? AppId => null;
    public string? ClusterId => null;
    public string? ContentEncoding => null;
    public string? ContentType => null;
    public string? CorrelationId => null;
    public DeliveryModes DeliveryMode => DeliveryModes.Persistent;
    public string? Expiration => null;
    public IDictionary<string, object?>? Headers => null;
    public string? MessageId => throw new ObjectDisposedException(nameof(IChannel));
    public bool Persistent => true;
    public byte Priority => 0;
    public string? ReplyTo => null;
    public PublicationAddress? ReplyToAddress => null;
    public AmqpTimestamp Timestamp => default;
    public string? Type => null;
    public string? UserId => null;

    public bool IsAppIdPresent() => false;
    public bool IsClusterIdPresent() => false;
    public bool IsContentEncodingPresent() => false;
    public bool IsContentTypePresent() => false;
    public bool IsCorrelationIdPresent() => false;
    public bool IsDeliveryModePresent() => false;
    public bool IsExpirationPresent() => false;
    public bool IsHeadersPresent() => false;
    public bool IsMessageIdPresent() => true;
    public bool IsPriorityPresent() => false;
    public bool IsReplyToPresent() => false;
    public bool IsTimestampPresent() => false;
    public bool IsTypePresent() => false;
    public bool IsUserIdPresent() => false;
  }
}
