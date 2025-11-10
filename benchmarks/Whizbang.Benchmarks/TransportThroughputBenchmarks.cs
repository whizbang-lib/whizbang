using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Transport layer throughput benchmarks measuring sustained message routing performance.
/// Focuses on transport mechanics: subscription management, routing, delivery.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn]
public class TransportThroughputBenchmarks {
  private ITransportManager _transportManager = null!;
  private ITransport _transport = null!;
  private ServiceProvider _serviceProvider = null!;

  private record TestCommand(string Id, int Value);
  private List<IMessageEnvelope> _testEnvelopes = null!;

  [GlobalSetup]
  public void Setup() {
    var services = new ServiceCollection();
    services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();
    services.AddSingleton<ITransportManager, TransportManager>();
    services.AddSingleton<ITransport, InProcessTransport>();

    _serviceProvider = services.BuildServiceProvider();
    _transportManager = _serviceProvider.GetRequiredService<ITransportManager>();
    _transport = _serviceProvider.GetRequiredService<ITransport>();

    // Pre-generate test envelopes
    _testEnvelopes = Enumerable.Range(0, 100_000)
      .Select(i => CreateEnvelope(new TestCommand($"cmd-{i}", i)))
      .ToList();
  }

  [GlobalCleanup]
  public void Cleanup() {
    _serviceProvider?.Dispose();
  }

  /// <summary>
  /// Baseline: Single topic, single subscriber, 100K messages.
  /// Pure transport overhead without concurrency.
  /// </summary>
  [Benchmark(Baseline = true)]
  [Arguments(100_000)]
  public async Task SingleTopic_SingleSubscriber_100K_Messages(int messageCount) {
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();
    var topic = "transport-single";

    await _transportManager.SubscribeAsync<TestCommand>(topic, async envelope => {
      if (Interlocked.Increment(ref receivedCount) >= messageCount) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    });

    for (int i = 0; i < messageCount; i++) {
      await _transport.PublishAsync(_testEnvelopes[i], new TransportDestination { Topic = topic });
    }

    await tcs.Task;
    await _transportManager.UnsubscribeAsync<TestCommand>(topic);
  }

  /// <summary>
  /// Single topic, multiple subscribers (fan-out pattern).
  /// Measures multi-cast delivery overhead.
  /// </summary>
  [Benchmark]
  [Arguments(50_000, 5)] // 50K messages to 5 subscribers = 250K deliveries
  public async Task SingleTopic_MultipleSubscribers_FanOut(int messageCount, int subscriberCount) {
    var topic = "transport-fanout";
    var expectedTotal = messageCount * subscriberCount;
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();

    // Subscribe multiple handlers to same topic
    for (int i = 0; i < subscriberCount; i++) {
      await _transportManager.SubscribeAsync<TestCommand>(topic, async envelope => {
        if (Interlocked.Increment(ref receivedCount) >= expectedTotal) {
          tcs.TrySetResult(true);
        }
        await Task.CompletedTask;
      });
    }

    // Publish messages
    for (int i = 0; i < messageCount; i++) {
      await _transport.PublishAsync(_testEnvelopes[i], new TransportDestination { Topic = topic });
    }

    await tcs.Task;
    await _transportManager.UnsubscribeAsync<TestCommand>(topic);
  }

  /// <summary>
  /// Multiple topics with dedicated subscribers.
  /// Measures routing overhead across topics.
  /// </summary>
  [Benchmark]
  [Arguments(10, 10_000)] // 10 topics, 10K messages each
  public async Task MultipleTopics_DedicatedSubscribers(int topicCount, int messagesPerTopic) {
    var totalMessages = topicCount * messagesPerTopic;
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();

    var topics = Enumerable.Range(0, topicCount)
      .Select(i => $"transport-topic-{i}")
      .ToList();

    // Subscribe to each topic
    foreach (var topic in topics) {
      await _transportManager.SubscribeAsync<TestCommand>(topic, async envelope => {
        if (Interlocked.Increment(ref receivedCount) >= totalMessages) {
          tcs.TrySetResult(true);
        }
        await Task.CompletedTask;
      });
    }

    // Publish round-robin to topics
    for (int i = 0; i < totalMessages; i++) {
      var topic = topics[i % topicCount];
      await _transport.PublishAsync(_testEnvelopes[i % _testEnvelopes.Count],
        new TransportDestination { Topic = topic });
    }

    await tcs.Task;

    foreach (var topic in topics) {
      await _transportManager.UnsubscribeAsync<TestCommand>(topic);
    }
  }

  /// <summary>
  /// High-frequency subscribe/unsubscribe operations.
  /// Measures subscription management overhead.
  /// </summary>
  [Benchmark]
  [Arguments(10_000)]
  public async Task SubscribeUnsubscribe_10K_Operations(int operationCount) {
    var topic = "transport-subscribe-test";

    for (int i = 0; i < operationCount; i++) {
      await _transportManager.SubscribeAsync<TestCommand>(topic, async envelope => {
        await Task.CompletedTask;
      });

      await _transportManager.UnsubscribeAsync<TestCommand>(topic);
    }
  }

  /// <summary>
  /// Concurrent publishers to same topic.
  /// Measures thread contention in transport layer.
  /// </summary>
  [Benchmark]
  [Arguments(10, 10_000)] // 10 publishers, 10K each = 100K total
  public async Task ConcurrentPublishers_SameTopic(int publisherCount, int messagesPerPublisher) {
    var totalMessages = publisherCount * messagesPerPublisher;
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();
    var topic = "transport-concurrent-pub";

    await _transportManager.SubscribeAsync<TestCommand>(topic, async envelope => {
      if (Interlocked.Increment(ref receivedCount) >= totalMessages) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    });

    // Concurrent publishers
    var publishTasks = Enumerable.Range(0, publisherCount)
      .Select(async publisherId => {
        var start = publisherId * messagesPerPublisher;
        for (int i = 0; i < messagesPerPublisher; i++) {
          var index = start + i;
          await _transport.PublishAsync(_testEnvelopes[index % _testEnvelopes.Count],
            new TransportDestination { Topic = topic });
        }
      });

    await Task.WhenAll(publishTasks);
    await tcs.Task;
    await _transportManager.UnsubscribeAsync<TestCommand>(topic);
  }

  /// <summary>
  /// Batched publishing (fire-and-forget style).
  /// Measures throughput without waiting for handlers.
  /// </summary>
  [Benchmark]
  [Arguments(100_000)]
  public async Task BatchedPublish_100K_FireAndForget(int messageCount) {
    var topic = "transport-batched";
    var receivedCount = 0;

    await _transportManager.SubscribeAsync<TestCommand>(topic, async envelope => {
      Interlocked.Increment(ref receivedCount);
      await Task.CompletedTask;
    });

    // Publish all messages without waiting
    var publishTasks = new List<Task>();
    for (int i = 0; i < messageCount; i++) {
      publishTasks.Add(_transport.PublishAsync(_testEnvelopes[i],
        new TransportDestination { Topic = topic }));
    }

    await Task.WhenAll(publishTasks);

    // Wait a bit for all to be delivered
    while (Volatile.Read(ref receivedCount) < messageCount) {
      await Task.Delay(1);
    }

    await _transportManager.UnsubscribeAsync<TestCommand>(topic);
  }

  /// <summary>
  /// With routing keys (pattern matching overhead).
  /// </summary>
  [Benchmark]
  [Arguments(50_000)]
  public async Task WithRoutingKeys_50K_Messages(int messageCount) {
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();
    var topic = "transport.routing.test";

    await _transportManager.SubscribeAsync<TestCommand>(topic, async envelope => {
      if (Interlocked.Increment(ref receivedCount) >= messageCount) {
        tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    }, "*.routing.*");

    for (int i = 0; i < messageCount; i++) {
      await _transport.PublishAsync(_testEnvelopes[i], new TransportDestination {
        Topic = topic,
        RoutingKey = $"key.routing.{i % 10}"
      });
    }

    await tcs.Task;
    await _transportManager.UnsubscribeAsync<TestCommand>(topic);
  }

  /// <summary>
  /// Mixed topic and routing key scenario.
  /// </summary>
  [Benchmark]
  [Arguments(10, 5_000)] // 10 topics, 5K messages each
  public async Task MixedTopicsAndRoutingKeys(int topicCount, int messagesPerTopic) {
    var totalMessages = topicCount * messagesPerTopic;
    var receivedCount = 0;
    var tcs = new TaskCompletionSource<bool>();

    var topics = Enumerable.Range(0, topicCount)
      .Select(i => $"transport.topic-{i}")
      .ToList();

    // Subscribe to each topic with routing key pattern
    foreach (var topic in topics) {
      await _transportManager.SubscribeAsync<TestCommand>(topic, async envelope => {
        if (Interlocked.Increment(ref receivedCount) >= totalMessages) {
          tcs.TrySetResult(true);
        }
        await Task.CompletedTask;
      }, "*.key.*");
    }

    // Publish with routing keys
    for (int i = 0; i < totalMessages; i++) {
      var topicIndex = i % topicCount;
      var topic = topics[topicIndex];
      await _transport.PublishAsync(_testEnvelopes[i % _testEnvelopes.Count], new TransportDestination {
        Topic = topic,
        RoutingKey = $"prefix.key.{i % 5}"
      });
    }

    await tcs.Task;

    foreach (var topic in topics) {
      await _transportManager.UnsubscribeAsync<TestCommand>(topic);
    }
  }

  private IMessageEnvelope CreateEnvelope(TestCommand command) {
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = command,
      Hops = new List<MessageHop>()
    };

    envelope.AddHop(new MessageHop {
      Type = MessageHopType.Current,
      ServiceName = "TransportBenchmark",
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });

    return envelope;
  }
}
