using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.AzureServiceBus.Tests.Containers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Integration tests proving FIFO ordering guarantees through Azure Service Bus sessions.
/// Uses the ASB emulator with session-enabled topics pre-configured in Config.json.
/// All tests use deterministic completion signals (Channel&lt;T&gt;) — no Task.Delay polling.
/// </summary>
[Timeout(240_000)]
[Category("Integration")]
[NotInParallel("ServiceBus")]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class AzureServiceBusFifoIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  // ========================================
  // SINGLE-MESSAGE FIFO TESTS
  // ========================================

  [Test]
  public async Task SinglePublish_50Messages_SameStream_ReceivedInMessageIdOrderAsync() {
    // Arrange — publish 50 messages with the same StreamId, verify FIFO
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new AzureServiceBusOptions { EnableSessions = true, AutoProvisionInfrastructure = true };
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);
    await transport.InitializeAsync();

    var streamId = Guid.CreateVersion7();
    var destination = new TransportDestination("topic-fifo-01");
    var subscribeDestination = new TransportDestination("topic-fifo-01", "sub-fifo-session");

    // Channel to collect received MessageIds in order
    var receivedChannel = Channel.CreateUnbounded<Guid>();
    var expectedIds = new List<Guid>(50);

    // Subscribe with session processor
    var subscription = await transport.SubscribeAsync(
      async (envelope, _, ct) => {
        await receivedChannel.Writer.WriteAsync(envelope.MessageId.Value, ct);
      },
      subscribeDestination
    );

    try {
      // Act — publish 50 messages sequentially with same StreamId
      for (var i = 0; i < 50; i++) {
        var envelope = _createTestEnvelope($"fifo-msg-{i}");
        expectedIds.Add(envelope.MessageId.Value);

        // Carry StreamId in metadata (as TransportPublishStrategy does)
        var destWithStream = new TransportDestination(
          destination.Address,
          destination.RoutingKey,
          new Dictionary<string, System.Text.Json.JsonElement> {
            ["StreamId"] = System.Text.Json.JsonDocument.Parse($"\"{streamId}\"").RootElement
          }
        );
        await transport.PublishAsync(envelope, destWithStream, cancellationToken: CancellationToken.None);
      }

      // Assert — read 50 messages from channel, verify order matches publish order
      var receivedIds = new List<Guid>(50);
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

      for (var i = 0; i < 50; i++) {
        var id = await receivedChannel.Reader.ReadAsync(cts.Token);
        receivedIds.Add(id);
      }

      // Verify per-stream FIFO: received order matches sent order
      for (var i = 0; i < 50; i++) {
        await Assert.That(receivedIds[i]).IsEqualTo(expectedIds[i])
          .Because($"Message at position {i} should match sent order for FIFO guarantee");
      }
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task SinglePublish_100Messages_3Streams_EachStreamReceivedInOrderAsync() {
    // Arrange — 3 streams with ~33 messages each, verify per-stream FIFO
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new AzureServiceBusOptions { EnableSessions = true, MaxConcurrentSessions = 3, AutoProvisionInfrastructure = true };
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);
    await transport.InitializeAsync();

    var streams = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
    var destination = new TransportDestination("topic-fifo-02");
    var subscribeDestination = new TransportDestination("topic-fifo-02", "sub-fifo-session");

    // Track per-stream ordering
    var receivedPerStream = new Dictionary<Guid, Channel<Guid>>();
    var expectedPerStream = new Dictionary<Guid, List<Guid>>();
    foreach (var s in streams) {
      receivedPerStream[s] = Channel.CreateUnbounded<Guid>();
      expectedPerStream[s] = [];
    }

    var subscription = await transport.SubscribeAsync(
      async (envelope, _, ct) => {
        // Determine stream from SessionId in the hop or from envelope context
        var messageId = envelope.MessageId.Value;
        // Find which stream this message belongs to by checking expectedPerStream
        foreach (var (streamId, expected) in expectedPerStream) {
          if (expected.Contains(messageId)) {
            await receivedPerStream[streamId].Writer.WriteAsync(messageId, ct);
            return;
          }
        }
      },
      subscribeDestination
    );

    try {
      // Act — interleave messages across 3 streams
      for (var i = 0; i < 99; i++) {
        var streamIdx = i % 3;
        var streamId = streams[streamIdx];
        var envelope = _createTestEnvelope($"stream{streamIdx}-msg-{i / 3}");
        expectedPerStream[streamId].Add(envelope.MessageId.Value);

        var destWithStream = new TransportDestination(
          destination.Address,
          destination.RoutingKey,
          new Dictionary<string, System.Text.Json.JsonElement> {
            ["StreamId"] = System.Text.Json.JsonDocument.Parse($"\"{streamId}\"").RootElement
          }
        );
        await transport.PublishAsync(envelope, destWithStream, cancellationToken: CancellationToken.None);
      }

      // Assert — verify each stream received in order
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

      foreach (var (streamId, expected) in expectedPerStream) {
        var received = new List<Guid>();
        for (var i = 0; i < expected.Count; i++) {
          var id = await receivedPerStream[streamId].Reader.ReadAsync(cts.Token);
          received.Add(id);
        }

        for (var i = 0; i < expected.Count; i++) {
          await Assert.That(received[i]).IsEqualTo(expected[i])
            .Because($"Stream {streamId}: message at position {i} should match sent order");
        }
      }
    } finally {
      subscription.Dispose();
    }
  }

  // ========================================
  // BULK-PUBLISH FIFO TESTS
  // ========================================

  [Test]
  public async Task BulkPublish_50Messages_SameStream_ReceivedInMessageIdOrderAsync() {
    // Arrange — bulk publish 50 messages with same StreamId
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new AzureServiceBusOptions { EnableSessions = true, AutoProvisionInfrastructure = true };
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);
    await transport.InitializeAsync();

    var streamId = Guid.CreateVersion7();
    var destination = new TransportDestination("topic-fifo-01");
    var subscribeDestination = new TransportDestination("topic-fifo-01", "sub-fifo-session");

    var receivedChannel = Channel.CreateUnbounded<Guid>();

    var subscription = await transport.SubscribeAsync(
      async (envelope, _, ct) => {
        await receivedChannel.Writer.WriteAsync(envelope.MessageId.Value, ct);
      },
      subscribeDestination
    );

    try {
      // Build batch items
      var bulkItems = new List<BulkPublishItem>(50);
      var expectedIds = new List<Guid>(50);

      for (var i = 0; i < 50; i++) {
        var envelope = _createTestEnvelope($"bulk-fifo-{i}");
        expectedIds.Add(envelope.MessageId.Value);
        bulkItems.Add(new BulkPublishItem {
          Envelope = envelope,
          EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
          MessageId = envelope.MessageId.Value,
          RoutingKey = null,
          StreamId = streamId
        });
      }

      // Act — send all in one batch call
      var results = await transport.PublishBatchAsync(bulkItems, destination, CancellationToken.None);

      // Verify all published successfully
      foreach (var result in results) {
        await Assert.That(result.Success).IsTrue()
          .Because($"Message {result.MessageId} should publish successfully");
      }

      // Assert — read back in order
      var receivedIds = new List<Guid>(50);
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

      for (var i = 0; i < 50; i++) {
        var id = await receivedChannel.Reader.ReadAsync(cts.Token);
        receivedIds.Add(id);
      }

      for (var i = 0; i < 50; i++) {
        await Assert.That(receivedIds[i]).IsEqualTo(expectedIds[i])
          .Because($"Bulk publish: message at position {i} should match sent order for FIFO");
      }
    } finally {
      subscription.Dispose();
    }
  }

  // ========================================
  // SESSION PROCESSOR TESTS
  // ========================================

  [Test]
  public async Task SessionProcessor_SameStream_ProcessesSequentiallyAsync() {
    // Verify that MaxConcurrentCallsPerSession=1 means only one message at a time per session
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new AzureServiceBusOptions {
      EnableSessions = true,
      MaxConcurrentSessions = 1,
      AutoProvisionInfrastructure = true
    };
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);
    await transport.InitializeAsync();

    var streamId = Guid.CreateVersion7();
    var destination = new TransportDestination("topic-fifo-01");
    var subscribeDestination = new TransportDestination("topic-fifo-01", "sub-fifo-session");

    var concurrencyTracker = new ConcurrencyTracker();
    var allProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var processedCount = 0;
    const int messageCount = 10;

    var subscription = await transport.SubscribeAsync(
      async (envelope, _, ct) => {
        concurrencyTracker.Enter();
        try {
          // Simulate some work
          await Task.Delay(50, ct);
        } finally {
          concurrencyTracker.Exit();
          if (Interlocked.Increment(ref processedCount) >= messageCount) {
            allProcessed.TrySetResult();
          }
        }
      },
      subscribeDestination
    );

    try {
      // Publish 10 messages with same stream
      for (var i = 0; i < messageCount; i++) {
        var envelope = _createTestEnvelope($"concurrent-test-{i}");
        var destWithStream = new TransportDestination(
          destination.Address,
          destination.RoutingKey,
          new Dictionary<string, System.Text.Json.JsonElement> {
            ["StreamId"] = System.Text.Json.JsonDocument.Parse($"\"{streamId}\"").RootElement
          }
        );
        await transport.PublishAsync(envelope, destWithStream, cancellationToken: CancellationToken.None);
      }

      // Wait for all to be processed
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      cts.Token.Register(() => allProcessed.TrySetCanceled());
      await allProcessed.Task;

      // Assert — max concurrency within the session should be 1
      await Assert.That(concurrencyTracker.MaxConcurrency).IsEqualTo(1)
        .Because("MaxConcurrentCallsPerSession=1 should ensure sequential processing within a session");
    } finally {
      subscription.Dispose();
    }
  }

  // ========================================
  // CAPABILITIES TESTS
  // ========================================

  [Test]
  public async Task Capabilities_WithEnableSessions_IncludesOrdered_IntegrationAsync() {
    // Arrange
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new AzureServiceBusOptions { EnableSessions = true };
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);

    // Act
    var capabilities = transport.Capabilities;

    // Assert
    await Assert.That((capabilities & TransportCapabilities.Ordered) != 0).IsTrue();
  }

  [Test]
  public async Task Capabilities_WithoutEnableSessions_ExcludesOrdered_IntegrationAsync() {
    // Arrange
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new AzureServiceBusOptions { EnableSessions = false };
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);

    // Act
    var capabilities = transport.Capabilities;

    // Assert
    await Assert.That((capabilities & TransportCapabilities.Ordered) != 0).IsFalse();
  }

  // ========================================
  // HELPERS
  // ========================================

  private static MessageEnvelope<TestMessage> _createTestEnvelope(string content = "test") {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage(content),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "fifo-test",
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };
  }

  /// <summary>
  /// Tracks concurrent access to verify MaxConcurrentCallsPerSession=1.
  /// Thread-safe — uses Interlocked for accuracy.
  /// </summary>
  private sealed class ConcurrencyTracker {
    private int _current;
    private int _max;

    public int MaxConcurrency => _max;

    public void Enter() {
      var current = Interlocked.Increment(ref _current);
      // Update max using compare-and-swap loop
      int max;
      do {
        max = _max;
        if (current <= max) {
          break;
        }
      } while (Interlocked.CompareExchange(ref _max, current, max) != max);
    }

    public void Exit() {
      Interlocked.Decrement(ref _current);
    }
  }
}
