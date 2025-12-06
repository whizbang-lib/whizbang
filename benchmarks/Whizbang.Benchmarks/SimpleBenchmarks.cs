using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Whizbang.Core.Observability;
using Whizbang.Core.Sequencing;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Simple benchmarks for core Whizbang components.
/// Focuses on most important performance characteristics.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class SimpleBenchmarks {
  private record TestCommand(string Id, int Value);
  private ISequenceProvider _sequenceProvider = null!;
  private InMemoryTraceStore _traceStore = null!;

  [GlobalSetup]
  public void Setup() {
    _sequenceProvider = new InMemorySequenceProvider();
    _traceStore = new InMemoryTraceStore();
  }

  // ID Generation Benchmarks
  [Benchmark]
  public static MessageId CreateMessageId() {
    return MessageId.New();
  }

  [Benchmark]
  public static CorrelationId CreateCorrelationId() {
    return CorrelationId.New();
  }

  // Sequence Provider Benchmarks
  [Benchmark]
  public async Task<long> GetNextSequence() {
    return await _sequenceProvider.GetNextAsync("test-stream");
  }

  [Benchmark]
  public async Task GetNextSequence_100Times() {
    for (int i = 0; i < 100; i++) {
      await _sequenceProvider.GetNextAsync("test-stream");
    }
  }

  // Message Hop Benchmarks
  [Benchmark]
  public static MessageHop CreateSimpleHop() {
    return new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "benchmark-host",
        ProcessId = 12345
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow
    };
  }

  [Benchmark]
  public static MessageHop CreateComplexHop() {
    return new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "benchmark-host",
        ProcessId = 12345
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      Topic = "test-topic",
      StreamKey = "stream-123",
      PartitionIndex = 0,
      SequenceNumber = 1
    };
  }

  // Message Envelope Benchmarks
  [Benchmark]
  public static IMessageEnvelope CreateEnvelope() {
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
        HostName = "benchmark-host",
        ProcessId = 12345
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow
    });
    return envelope;
  }

  // TraceStore Benchmarks
  [Benchmark]
  public async Task TraceStore_Store10Envelopes() {
    for (int i = 0; i < 10; i++) {
      var envelope = CreateEnvelope();
      await _traceStore.StoreAsync(envelope);
    }
  }

  [Benchmark]
  public async Task TraceStore_StoreAndRetrieve() {
    var envelope = CreateEnvelope();
    await _traceStore.StoreAsync(envelope);
    var retrieved = await _traceStore.GetByMessageIdAsync(envelope.MessageId);
  }
}
