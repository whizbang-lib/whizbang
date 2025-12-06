using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;
using Whizbang.Core.Partitioning;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Benchmarks for partition routers.
/// Measures partition assignment performance and distribution quality.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class PartitionRouterBenchmarks {
  private record TestCommand(string OrderId);

  private IPartitionRouter _router = null!;
  private PolicyContext _context = null!;
  private const int PartitionCount = 16;

  [GlobalSetup]
  public void Setup() {
    _router = new HashPartitionRouter();

    var message = new TestCommand("order-123");
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = []
    };
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Benchmark",
        InstanceId = Guid.NewGuid(),
        HostName = "benchmark-host",
        ProcessId = 12345
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });

    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();
    _context = new PolicyContext(
      message: message,
      envelope: envelope,
      services: serviceProvider,
      environment: "benchmark"
    );
  }

  [Benchmark(Baseline = true)]
  public int SinglePartitionAssignment() {
    return _router.SelectPartition("order-12345", PartitionCount, _context);
  }

  [Benchmark]
  public void Assign1000Keys() {
    for (int i = 0; i < 1000; i++) {
      _router.SelectPartition($"order-{i}", PartitionCount, _context);
    }
  }

  [Benchmark]
  public void AssignVariedKeys_1000() {
    // Simulate realistic key patterns
    for (int i = 0; i < 1000; i++) {
      var key = (i % 3) switch {
        0 => $"order-{i}",
        1 => $"user-{i / 10}",
        _ => $"tenant-{i / 100}-order-{i}"
      };
      _router.SelectPartition(key, PartitionCount, _context);
    }
  }

  [Benchmark]
  public void ParallelAssignment_10000Keys() {
    Parallel.For(0, 10000, i => {
      _router.SelectPartition($"order-{i}", PartitionCount, _context);
    });
  }

  [Benchmark]
  public void DifferentPartitionCounts() {
    // Test with varying partition counts
    var counts = new[] { 4, 8, 16, 32, 64 };
    foreach (var count in counts) {
      for (int i = 0; i < 100; i++) {
        _router.SelectPartition($"order-{i}", count, _context);
      }
    }
  }
}
