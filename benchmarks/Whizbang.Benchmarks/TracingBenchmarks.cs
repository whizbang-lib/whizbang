using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Benchmarks for tracing and observability overhead.
/// Measures cost of hop creation, envelope construction, and metadata operations.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class TracingBenchmarks {
  private const string BENCHMARK_HOST = "benchmark-host";
  private const int BENCHMARK_PROCESS_ID = 12345;

  private sealed record TestCommand(string Id, int Value);

  [Benchmark(Baseline = true)]
  public static MessageId CreateMessageId() {
    return MessageId.New();
  }

  [Benchmark]
  public static CorrelationId CreateCorrelationId() {
    return CorrelationId.New();
  }

  [Benchmark]
  public static MessageHop CreateMessageHop() {
    return new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = BENCHMARK_HOST,
        ProcessId = BENCHMARK_PROCESS_ID
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      Topic = "test-topic",
      StreamKey = "stream-123",
      PartitionIndex = 0,
      SequenceNumber = 1
    };
  }

  [Benchmark]
  public static IMessageEnvelope CreateMessageEnvelope() {
    var message = new TestCommand("test-123", 42);
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = []
    };
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = BENCHMARK_HOST,
        ProcessId = BENCHMARK_PROCESS_ID
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow
    });
    return envelope;
  }

  [Benchmark]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public static IMessageEnvelope CreateEnvelopeWithMetadata() {
    var message = new TestCommand("test-123", 42);
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = []
    };

    var metadata = new Dictionary<string, JsonElement>();
    // Add 10 metadata entries
    for (int i = 0; i < 10; i++) {
      metadata[$"key{i}"] = JsonSerializer.SerializeToElement($"value{i}");
    }

    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = BENCHMARK_HOST,
        ProcessId = BENCHMARK_PROCESS_ID
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      Metadata = metadata
    };

    envelope.AddHop(hop);
    return envelope;
  }

  [Benchmark]
  public static IMessageEnvelope CreateEnvelopeWith3Hops() {
    var message = new TestCommand("test-123", 42);
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = []
    };

    // Add 3 hops (current + 2 causation)
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service1",
        InstanceId = Guid.NewGuid(),
        HostName = BENCHMARK_HOST,
        ProcessId = BENCHMARK_PROCESS_ID
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow
    });
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service2",
        InstanceId = Guid.NewGuid(),
        HostName = BENCHMARK_HOST,
        ProcessId = BENCHMARK_PROCESS_ID
      },
      Type = HopType.Causation,
      Timestamp = DateTimeOffset.UtcNow.AddSeconds(-1),
      CausationId = MessageId.New(),
      CausationType = "ParentCommand"
    });
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service3",
        InstanceId = Guid.NewGuid(),
        HostName = BENCHMARK_HOST,
        ProcessId = BENCHMARK_PROCESS_ID
      },
      Type = HopType.Causation,
      Timestamp = DateTimeOffset.UtcNow.AddSeconds(-2),
      CausationId = MessageId.New(),
      CausationType = "GrandparentCommand"
    });

    return envelope;
  }

  [Benchmark]
  public string? GetCurrentTopicFromEnvelope() {
    var envelope = _createTypedEnvelope();
    return envelope.GetCurrentTopic();
  }

  [Benchmark]
  public SecurityContext? GetCurrentSecurityContextFromEnvelope() {
    var envelope = _createTypedEnvelope();
    return envelope.GetCurrentSecurityContext();
  }

  [Benchmark]
  public IReadOnlyList<MessageHop> GetCausationHopsFromEnvelope() {
    var envelope = _createEnvelopeWith3HopsTyped();
    return envelope.GetCausationHops();
  }

  [Benchmark]
  public async Task TraceStore_Store1000EnvelopesAsync() {
    var store = new InMemoryTraceStore();
    var tasks = new List<Task>();

    for (int i = 0; i < 1000; i++) {
      var envelope = CreateMessageEnvelope();
      tasks.Add(store.StoreAsync(envelope));
    }

    await Task.WhenAll(tasks);
  }

  [Benchmark]
  public static void PolicyDecisionTrail_Record100Decisions() {
    var trail = new PolicyDecisionTrail();

    for (int i = 0; i < 100; i++) {
      trail.RecordDecision(
        policyName: $"Policy{i}",
        rule: "predicate",
        matched: i % 2 == 0,
        configuration: null,
        reason: $"Test reason {i}"
      );
    }
  }

  // Helper methods to return typed envelopes for method access
  private static MessageEnvelope<TestCommand> _createTypedEnvelope() {
    var message = new TestCommand("test-123", 42);
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = []
    };
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = BENCHMARK_HOST,
        ProcessId = BENCHMARK_PROCESS_ID
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      Topic = "test-topic"
    });
    return envelope;
  }

  private static MessageEnvelope<TestCommand> _createEnvelopeWith3HopsTyped() {
    var message = new TestCommand("test-123", 42);
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = []
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service1",
        InstanceId = Guid.NewGuid(),
        HostName = BENCHMARK_HOST,
        ProcessId = BENCHMARK_PROCESS_ID
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow
    });
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service2",
        InstanceId = Guid.NewGuid(),
        HostName = BENCHMARK_HOST,
        ProcessId = BENCHMARK_PROCESS_ID
      },
      Type = HopType.Causation,
      Timestamp = DateTimeOffset.UtcNow.AddSeconds(-1)
    });
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service3",
        InstanceId = Guid.NewGuid(),
        HostName = BENCHMARK_HOST,
        ProcessId = BENCHMARK_PROCESS_ID
      },
      Type = HopType.Causation,
      Timestamp = DateTimeOffset.UtcNow.AddSeconds(-2)
    });

    return envelope;
  }
}
