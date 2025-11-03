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
[SimpleJob(RuntimeMoniker.Net90)]
[MemoryDiagnoser]
[MarkdownExporter]
public class TracingBenchmarks {
  private record TestCommand(string Id, int Value);

  [Benchmark(Baseline = true)]
  public MessageId CreateMessageId() {
    return MessageId.New();
  }

  [Benchmark]
  public CorrelationId CreateCorrelationId() {
    return CorrelationId.New();
  }

  [Benchmark]
  public CausationId CreateCausationId() {
    return CausationId.New();
  }

  [Benchmark]
  public MessageHop CreateMessageHop() {
    return new MessageHop {
      Type = HopType.Current,
      ServiceName = "TestService",
      Timestamp = DateTimeOffset.UtcNow,
      Topic = "test-topic",
      StreamKey = "stream-123",
      PartitionIndex = 0,
      SequenceNumber = 1
    };
  }

  [Benchmark]
  public IMessageEnvelope CreateMessageEnvelope() {
    var message = new TestCommand("test-123", 42);
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
      Payload = message,
      Hops = new List<MessageHop>()
    };
    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceName = "TestService",
      Timestamp = DateTimeOffset.UtcNow
    });
    return envelope;
  }

  [Benchmark]
  public IMessageEnvelope CreateEnvelopeWithMetadata() {
    var message = new TestCommand("test-123", 42);
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
      Payload = message,
      Hops = new List<MessageHop>()
    };

    var metadata = new Dictionary<string, object>();
    // Add 10 metadata entries
    for (int i = 0; i < 10; i++) {
      metadata[$"key{i}"] = $"value{i}";
    }

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceName = "TestService",
      Timestamp = DateTimeOffset.UtcNow,
      Metadata = metadata
    };

    envelope.AddHop(hop);
    return envelope;
  }

  [Benchmark]
  public IMessageEnvelope CreateEnvelopeWith3Hops() {
    var message = new TestCommand("test-123", 42);
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
      Payload = message,
      Hops = new List<MessageHop>()
    };

    // Add 3 hops (current + 2 causation)
    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceName = "Service1",
      Timestamp = DateTimeOffset.UtcNow
    });
    envelope.AddHop(new MessageHop {
      Type = HopType.Causation,
      ServiceName = "Service2",
      Timestamp = DateTimeOffset.UtcNow.AddSeconds(-1),
      CausationMessageId = MessageId.New(),
      CausationMessageType = "ParentCommand"
    });
    envelope.AddHop(new MessageHop {
      Type = HopType.Causation,
      ServiceName = "Service3",
      Timestamp = DateTimeOffset.UtcNow.AddSeconds(-2),
      CausationMessageId = MessageId.New(),
      CausationMessageType = "GrandparentCommand"
    });

    return envelope;
  }

  [Benchmark]
  public string? GetCurrentTopicFromEnvelope() {
    var envelope = CreateTypedEnvelope();
    return envelope.GetCurrentTopic();
  }

  [Benchmark]
  public SecurityContext? GetCurrentSecurityContextFromEnvelope() {
    var envelope = CreateTypedEnvelope();
    return envelope.GetCurrentSecurityContext();
  }

  [Benchmark]
  public IReadOnlyList<MessageHop> GetCausationHopsFromEnvelope() {
    var envelope = CreateEnvelopeWith3HopsTyped();
    return envelope.GetCausationHops();
  }

  [Benchmark]
  public async Task TraceStore_Store1000Envelopes() {
    var store = new InMemoryTraceStore();
    var tasks = new List<Task>();

    for (int i = 0; i < 1000; i++) {
      var envelope = CreateMessageEnvelope();
      tasks.Add(store.StoreAsync(envelope));
    }

    await Task.WhenAll(tasks);
  }

  [Benchmark]
  public void PolicyDecisionTrail_Record100Decisions() {
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
  private MessageEnvelope<TestCommand> CreateTypedEnvelope() {
    var message = new TestCommand("test-123", 42);
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
      Payload = message,
      Hops = new List<MessageHop>()
    };
    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceName = "TestService",
      Timestamp = DateTimeOffset.UtcNow,
      Topic = "test-topic"
    });
    return envelope;
  }

  private MessageEnvelope<TestCommand> CreateEnvelopeWith3HopsTyped() {
    var message = new TestCommand("test-123", 42);
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
      Payload = message,
      Hops = new List<MessageHop>()
    };

    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceName = "Service1",
      Timestamp = DateTimeOffset.UtcNow
    });
    envelope.AddHop(new MessageHop {
      Type = HopType.Causation,
      ServiceName = "Service2",
      Timestamp = DateTimeOffset.UtcNow.AddSeconds(-1)
    });
    envelope.AddHop(new MessageHop {
      Type = HopType.Causation,
      ServiceName = "Service3",
      Timestamp = DateTimeOffset.UtcNow.AddSeconds(-2)
    });

    return envelope;
  }
}
