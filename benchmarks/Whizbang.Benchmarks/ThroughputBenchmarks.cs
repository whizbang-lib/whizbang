using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
    _smallMessages = [.. Enumerable.Range(0, 100_000).Select(i => new SmallCommand($"cmd-{i}", i))];

    _mediumMessages = [.. Enumerable.Range(0, 100_000).Select(i => new MediumCommand($"cmd-{i}", i, $"data1-{i}", $"data2-{i}", $"data3-{i}"))];

    _largeMessages = [.. Enumerable.Range(0, 10_000).Select(i => new LargeCommand($"cmd-{i}", i, new string('x', 10_000)))];
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
    var subscription = await _transport.SubscribeAsync(async (envelope, ct) => {
      Interlocked.Increment(ref receivedCount);
      if (receivedCount >= messageCount) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    }, new TransportDestination(topic));

    // Publish messages
    for (int i = 0; i < messageCount; i++) {
      var envelope = CreateEnvelope(_smallMessages[i]);
      await _transport.PublishAsync(envelope, new TransportDestination(topic));
    }

    // Wait for all messages to be received
    await tcs.Task;

    subscription.Dispose();
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

    var subscription = await _transport.SubscribeAsync(async (envelope, ct) => {
      Interlocked.Increment(ref receivedCount);
      if (receivedCount >= messageCount) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    }, new TransportDestination(topic));

    for (int i = 0; i < messageCount; i++) {
      var envelope = CreateEnvelope(_mediumMessages[i]);
      await _transport.PublishAsync(envelope, new TransportDestination(topic));
    }

    await tcs.Task;
    subscription.Dispose();
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

    var subscription = await _transport.SubscribeAsync(async (envelope, ct) => {
      Interlocked.Increment(ref receivedCount);
      if (receivedCount >= messageCount) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    }, new TransportDestination(topic));

    for (int i = 0; i < messageCount; i++) {
      var envelope = CreateEnvelope(_largeMessages[i]);
      await _transport.PublishAsync(envelope, new TransportDestination(topic));
    }

    await tcs.Task;
    subscription.Dispose();
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

    var subscription = await _transport.SubscribeAsync(async (envelope, ct) => {
      if (Interlocked.Increment(ref receivedCount) >= totalMessages) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    }, new TransportDestination(topic));

    // Start multiple publishers concurrently
    var publishTasks = Enumerable.Range(0, publisherCount)
      .Select(async publisherId => {
        for (int i = 0; i < messagesPerPublisher; i++) {
          var msgIndex = (publisherId * messagesPerPublisher) + i;
          var envelope = CreateEnvelope(_smallMessages[msgIndex % _smallMessages.Count]);
          await _transport.PublishAsync(envelope, new TransportDestination(topic));
        }
      });

    await Task.WhenAll(publishTasks);
    await tcs.Task;
    subscription.Dispose();
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

    var subscriptions = new List<ISubscription>();
    foreach (var topic in topics) {
      var subscription = await _transport.SubscribeAsync(async (envelope, ct) => {
        if (Interlocked.Increment(ref receivedCount) >= totalMessages) {
          tcs.TrySetResult(true);
        }
        await Task.CompletedTask;
      }, new TransportDestination(topic));
      subscriptions.Add(subscription);
    }

    // Publish to all topics concurrently
    var publishTasks = topics.Select(async (topic, topicIndex) => {
      for (int i = 0; i < messagesPerTopic; i++) {
        var msgIndex = (topicIndex * messagesPerTopic) + i;
        var envelope = CreateEnvelope(_smallMessages[msgIndex % _smallMessages.Count]);
        await _transport.PublishAsync(envelope, new TransportDestination(topic));
      }
    });

    await Task.WhenAll(publishTasks);
    await tcs.Task;

    // Cleanup
    foreach (var subscription in subscriptions) {
      subscription.Dispose();
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
    var subscription = await _transport.SubscribeAsync(async (requestEnvelope, ct) => {
      // Simulate processing and send response
      var typedEnvelope = (MessageEnvelope<SmallCommand>)requestEnvelope;
      var response = new SmallCommand($"response-{typedEnvelope.Payload.Id}", typedEnvelope.Payload.Value * 2);
      var responseEnvelope = CreateEnvelope(response);

      // Get correlation ID from request
      var correlationId = requestEnvelope.Hops.Last().CorrelationId;
      if (correlationId.HasValue) {
        await store.SaveResponseAsync(correlationId.Value, responseEnvelope, CancellationToken.None);
      }

      await Task.CompletedTask;
    }, new TransportDestination(topic));

    // Send requests and wait for responses
    var requestTasks = Enumerable.Range(0, requestCount).Select(async i => {
      var correlationId = CorrelationId.New();
      var requestId = MessageId.New();

      await store.SaveRequestAsync(correlationId, requestId, TimeSpan.FromSeconds(10), CancellationToken.None);

      var request = _smallMessages[i % _smallMessages.Count];
      var envelope = CreateEnvelope(request, correlationId);

      await _transport.PublishAsync(envelope, new TransportDestination(topic));

      var response = await store.WaitForResponseAsync(correlationId, CancellationToken.None);
      return response;
    });

    await Task.WhenAll(requestTasks);
    subscription.Dispose();
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

    var subscription = await _transport.SubscribeAsync(async (envelope, ct) => {
      // Simulate tracing overhead
      await traceStore.StoreAsync(envelope, ct);

      if (Interlocked.Increment(ref receivedCount) >= messageCount) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    }, new TransportDestination(topic));

    for (int i = 0; i < messageCount; i++) {
      var envelope = CreateEnvelopeWithTracing(_smallMessages[i]);
      await _transport.PublishAsync(envelope, new TransportDestination(topic));
    }

    await tcs.Task;
    subscription.Dispose();
  }

  private static IMessageEnvelope CreateEnvelope<T>(T payload, CorrelationId? correlationId = null) {
    var envelope = new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = []
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "BenchmarkPublisher",
        InstanceId = Guid.NewGuid(),
        HostName = "benchmark-host",
        ProcessId = 12345
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = correlationId ?? CorrelationId.New(),
      CausationId = MessageId.New()
    });

    return envelope;
  }

  private static IMessageEnvelope CreateEnvelopeWithTracing<T>(T payload) {
    var envelope = new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = []
    };

    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "BenchmarkPublisher",
        InstanceId = Guid.NewGuid(),
        HostName = "benchmark-host",
        ProcessId = 12345
      },
      Type = HopType.Current,
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

  // Helper classes for benchmarking
  private class InMemoryTraceStore : ITraceStore {
    public Task StoreAsync(IMessageEnvelope envelope, CancellationToken cancellationToken = default) {
      // No-op storage for benchmarking
      return Task.CompletedTask;
    }

    public Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken cancellationToken = default) {
      return Task.FromResult<IMessageEnvelope?>(null);
    }

    public Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken cancellationToken = default) {
      return Task.FromResult(new List<IMessageEnvelope>());
    }

    public Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken cancellationToken = default) {
      return Task.FromResult(new List<IMessageEnvelope>());
    }

    public Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default) {
      return Task.FromResult(new List<IMessageEnvelope>());
    }
  }

  private class InMemoryRequestResponseStore : IRequestResponseStore {
    private readonly ConcurrentDictionary<CorrelationId, TaskCompletionSource<IMessageEnvelope>> _pending = new();

    public Task SaveRequestAsync(CorrelationId correlationId, MessageId requestId, TimeSpan timeout, CancellationToken cancellationToken = default) {
      _pending[correlationId] = new TaskCompletionSource<IMessageEnvelope>();
      return Task.CompletedTask;
    }

    public Task SaveResponseAsync(CorrelationId correlationId, IMessageEnvelope responseEnvelope, CancellationToken cancellationToken = default) {
      if (_pending.TryGetValue(correlationId, out var tcs)) {
        tcs.TrySetResult(responseEnvelope);
      }
      return Task.CompletedTask;
    }

    public async Task<IMessageEnvelope?> WaitForResponseAsync(CorrelationId correlationId, CancellationToken cancellationToken = default) {
      if (_pending.TryGetValue(correlationId, out var tcs)) {
        return await tcs.Task;
      }
      return null;
    }

    public async Task<MessageEnvelope<TMessage>?> WaitForResponseAsync<TMessage>(CorrelationId correlationId, CancellationToken cancellationToken = default) {
      if (_pending.TryGetValue(correlationId, out var tcs)) {
        var result = await tcs.Task;
        return result as MessageEnvelope<TMessage>;
      }
      return null;
    }

    public Task CleanupExpiredAsync(CancellationToken cancellationToken = default) {
      // No-op for benchmarking
      return Task.CompletedTask;
    }
  }
}
