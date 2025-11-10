using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Sustained throughput benchmarks measuring realistic messages/second in end-to-end scenarios.
/// These measure aggregate throughput over time, not individual operation latency.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn]
public class ThroughputBenchmarks {
  private ServiceProvider _serviceProvider = null!;
  private ITransportManager _transportManager = null!;
  private ITransport _transport = null!;
  private IMessageSerializer _serializer = null!;

  // Test message types
  private record SmallCommand(string Id, int Value);
  private record MediumCommand(string Id, int Value, string Data1, string Data2, string Data3);
  private record LargeCommand(string Id, int Value, string Payload);

  private List<SmallCommand> _smallMessages = null!;
  private List<MediumCommand> _mediumMessages = null!;
  private List<LargeCommand> _largeMessages = null!;

  [GlobalSetup]
  public void Setup() {
    var services = new ServiceCollection();
    services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();
    services.AddSingleton<ITransportManager, TransportManager>();
    services.AddSingleton<ITransport, InProcessTransport>();

    _serviceProvider = services.BuildServiceProvider();
    _transportManager = _serviceProvider.GetRequiredService<ITransportManager>();
    _transport = _serviceProvider.GetRequiredService<ITransport>();
    _serializer = _serviceProvider.GetRequiredService<IMessageSerializer>();

    // Pre-generate test messages
    _smallMessages = Enumerable.Range(0, 100_000)
      .Select(i => new SmallCommand($"cmd-{i}", i))
      .ToList();

    _mediumMessages = Enumerable.Range(0, 100_000)
      .Select(i => new MediumCommand($"cmd-{i}", i, $"data1-{i}", $"data2-{i}", $"data3-{i}"))
      .ToList();

    _largeMessages = Enumerable.Range(0, 10_000)
      .Select(i => new LargeCommand($"cmd-{i}", i, new string('x', 10_000)))
      .ToList();
  }

  [GlobalCleanup]
  public void Cleanup() {
    _serviceProvider?.Dispose();
  }

  /// <summary>
  /// Baseline: Sustained throughput for 100K small messages through InProcessTransport.
  /// Measures: Full publish/subscribe cycle with serialization.
  /// </summary>
  [Benchmark(Baseline = true)]
  [Arguments(100_000)]
  public async Task Throughput_InProcessTransport_100K_SmallMessages(int messageCount) {
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();
    var topic = "throughput-test";

    // Subscribe
    await _transportManager.SubscribeAsync<SmallCommand>(topic, async envelope => {
      Interlocked.Increment(ref receivedCount);
      if (receivedCount >= messageCount) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    });

    // Publish messages
    for (int i = 0; i < messageCount; i++) {
      var envelope = CreateEnvelope(_smallMessages[i]);
      await _transport.PublishAsync(envelope, new TransportDestination { Topic = topic });
    }

    // Wait for all messages to be received
    await tcs.Task;

    await _transportManager.UnsubscribeAsync<SmallCommand>(topic);
  }

  /// <summary>
  /// Throughput for medium-sized messages (more realistic payload size).
  /// </summary>
  [Benchmark]
  [Arguments(50_000)]
  public async Task Throughput_InProcessTransport_50K_MediumMessages(int messageCount) {
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();
    var topic = "throughput-medium";

    await _transportManager.SubscribeAsync<MediumCommand>(topic, async envelope => {
      Interlocked.Increment(ref receivedCount);
      if (receivedCount >= messageCount) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    });

    for (int i = 0; i < messageCount; i++) {
      var envelope = CreateEnvelope(_mediumMessages[i]);
      await _transport.PublishAsync(envelope, new TransportDestination { Topic = topic });
    }

    await tcs.Task;
    await _transportManager.UnsubscribeAsync<MediumCommand>(topic);
  }

  /// <summary>
  /// Throughput for large messages (10KB payloads).
  /// </summary>
  [Benchmark]
  [Arguments(10_000)]
  public async Task Throughput_InProcessTransport_10K_LargeMessages(int messageCount) {
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();
    var topic = "throughput-large";

    await _transportManager.SubscribeAsync<LargeCommand>(topic, async envelope => {
      Interlocked.Increment(ref receivedCount);
      if (receivedCount >= messageCount) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    });

    for (int i = 0; i < messageCount; i++) {
      var envelope = CreateEnvelope(_largeMessages[i]);
      await _transport.PublishAsync(envelope, new TransportDestination { Topic = topic });
    }

    await tcs.Task;
    await _transportManager.UnsubscribeAsync<LargeCommand>(topic);
  }

  /// <summary>
  /// Concurrent throughput: Multiple publishers sending to single topic.
  /// Measures aggregate throughput across publishers.
  /// </summary>
  [Benchmark]
  [Arguments(10, 10_000)] // 10 publishers, 10K messages each = 100K total
  public async Task Throughput_Concurrent_MultiplePublishers(int publisherCount, int messagesPerPublisher) {
    var totalMessages = publisherCount * messagesPerPublisher;
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();
    var topic = "throughput-concurrent";

    await _transportManager.SubscribeAsync<SmallCommand>(topic, async envelope => {
      if (Interlocked.Increment(ref receivedCount) >= totalMessages) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    });

    // Start multiple publishers concurrently
    var publishTasks = Enumerable.Range(0, publisherCount)
      .Select(async publisherId => {
        for (int i = 0; i < messagesPerPublisher; i++) {
          var msgIndex = (publisherId * messagesPerPublisher) + i;
          var envelope = CreateEnvelope(_smallMessages[msgIndex % _smallMessages.Count]);
          await _transport.PublishAsync(envelope, new TransportDestination { Topic = topic });
        }
      });

    await Task.WhenAll(publishTasks);
    await tcs.Task;
    await _transportManager.UnsubscribeAsync<SmallCommand>(topic);
  }

  /// <summary>
  /// Concurrent throughput: Multiple topics with subscribers.
  /// Measures aggregate throughput across topics.
  /// </summary>
  [Benchmark]
  [Arguments(10, 10_000)] // 10 topics, 10K messages each = 100K total
  public async Task Throughput_Concurrent_MultipleTopics(int topicCount, int messagesPerTopic) {
    var totalMessages = topicCount * messagesPerTopic;
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();

    // Subscribe to all topics
    var topics = Enumerable.Range(0, topicCount)
      .Select(i => $"throughput-topic-{i}")
      .ToList();

    foreach (var topic in topics) {
      await _transportManager.SubscribeAsync<SmallCommand>(topic, async envelope => {
        if (Interlocked.Increment(ref receivedCount) >= totalMessages) {
          tcs.TrySetResult(true);
        }
        await Task.CompletedTask;
      });
    }

    // Publish to all topics concurrently
    var publishTasks = topics.Select(async (topic, topicIndex) => {
      for (int i = 0; i < messagesPerTopic; i++) {
        var msgIndex = (topicIndex * messagesPerTopic) + i;
        var envelope = CreateEnvelope(_smallMessages[msgIndex % _smallMessages.Count]);
        await _transport.PublishAsync(envelope, new TransportDestination { Topic = topic });
      }
    });

    await Task.WhenAll(publishTasks);
    await tcs.Task;

    // Cleanup
    foreach (var topic in topics) {
      await _transportManager.UnsubscribeAsync<SmallCommand>(topic);
    }
  }

  /// <summary>
  /// Request-Response throughput (sync-over-async pattern).
  /// Measures: Full request-response cycle with correlation.
  /// </summary>
  [Benchmark]
  [Arguments(10_000)]
  public async Task Throughput_RequestResponse_10K_Requests(int requestCount) {
    var store = new InMemoryRequestResponseStore();
    var topic = "throughput-request-response";

    // Setup response handler
    await _transportManager.SubscribeAsync<SmallCommand>(topic, async requestEnvelope => {
      // Simulate processing and send response
      var response = new SmallCommand($"response-{requestEnvelope.Payload.Id}", requestEnvelope.Payload.Value * 2);
      var responseEnvelope = CreateEnvelope(response);

      // Get correlation ID from request
      var correlationId = requestEnvelope.Hops.Last().CorrelationId;
      await store.SaveResponseAsync(correlationId, responseEnvelope, CancellationToken.None);

      await Task.CompletedTask;
    });

    // Send requests and wait for responses
    var requestTasks = Enumerable.Range(0, requestCount).Select(async i => {
      var correlationId = CorrelationId.New();
      var requestId = MessageId.New();

      await store.SaveRequestAsync(correlationId, requestId, TimeSpan.FromSeconds(10), CancellationToken.None);

      var request = _smallMessages[i % _smallMessages.Count];
      var envelope = CreateEnvelope(request, correlationId);

      await _transport.PublishAsync(envelope, new TransportDestination { Topic = topic });

      var response = await store.WaitForResponseAsync(correlationId, CancellationToken.None);
      return response;
    });

    await Task.WhenAll(requestTasks);
    await _transportManager.UnsubscribeAsync<SmallCommand>(topic);
  }

  /// <summary>
  /// With tracing enabled (realistic production scenario).
  /// </summary>
  [Benchmark]
  [Arguments(50_000)]
  public async Task Throughput_WithTracing_50K_Messages(int messageCount) {
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();
    var topic = "throughput-tracing";
    var traceStore = new InMemoryTraceStore();

    await _transportManager.SubscribeAsync<SmallCommand>(topic, async envelope => {
      // Simulate tracing overhead
      var trace = MessageTrace.FromEnvelope(envelope);
      await traceStore.StoreAsync(trace);

      if (Interlocked.Increment(ref receivedCount) >= messageCount) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    });

    for (int i = 0; i < messageCount; i++) {
      var envelope = CreateEnvelopeWithTracing(_smallMessages[i]);
      await _transport.PublishAsync(envelope, new TransportDestination { Topic = topic });
    }

    await tcs.Task;
    await _transportManager.UnsubscribeAsync<SmallCommand>(topic);
  }

  private IMessageEnvelope CreateEnvelope<T>(T payload, CorrelationId? correlationId = null) {
    var envelope = new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = new List<MessageHop>()
    };

    envelope.AddHop(new MessageHop {
      Type = MessageHopType.Current,
      ServiceName = "BenchmarkPublisher",
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = correlationId ?? CorrelationId.New(),
      CausationId = MessageId.New()
    });

    return envelope;
  }

  private IMessageEnvelope CreateEnvelopeWithTracing<T>(T payload) {
    var envelope = new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = new List<MessageHop>()
    };

    var hop = new MessageHop {
      Type = MessageHopType.Current,
      ServiceName = "BenchmarkPublisher",
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Topic = "throughput-tracing",
      SequenceNumber = 1,
      Metadata = new Dictionary<string, object> {
        ["traceId"] = Guid.NewGuid().ToString(),
        ["spanId"] = Guid.NewGuid().ToString()
      }
    };

    envelope.AddHop(hop);
    return envelope;
  }
}
