using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Execution;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Benchmarks comparing Task-based vs ValueTask-based execution patterns.
/// Focuses on zero-allocation goals for SerialExecutor and ParallelExecutor.
/// These benchmarks measure realistic message processing scenarios.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class ZeroAllocationBenchmarks {
  private IExecutionStrategy _serialExecutor = null!;
  private IExecutionStrategy _parallelExecutor = null!;
  private IMessageEnvelope _lightweightEnvelope = null!;
  private IMessageEnvelope _heavyweightEnvelope = null!;
  private PolicyContext _context = null!;

  // Test message types
  private record LightweightCommand(int Id, string Action);
  private record HeavyweightCommand(
    int Id,
    string Action,
    Dictionary<string, string> Metadata,
    List<string> Tags,
    DateTimeOffset Timestamp
  );

  [GlobalSetup]
  public async Task Setup() {
    _serialExecutor = new SerialExecutor();
    _parallelExecutor = new ParallelExecutor(maxConcurrency: 10);

    await _serialExecutor.StartAsync();
    await _parallelExecutor.StartAsync();

    // Lightweight message (minimal allocations)
    var lightMessage = new LightweightCommand(1, "process");
    _lightweightEnvelope = new MessageEnvelope<LightweightCommand> {
      MessageId = MessageId.New(),
      Payload = lightMessage,
      Hops = []
    };
    _lightweightEnvelope.AddHop(new MessageHop {
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

    // Heavyweight message (more realistic allocations)
    var heavyMessage = new HeavyweightCommand(
      Id: 1,
      Action: "process",
      Metadata: new Dictionary<string, string> {
        ["userId"] = "user-123",
        ["tenantId"] = "tenant-456",
        ["source"] = "api"
      },
      Tags: ["priority", "urgent", "customer-facing"],
      Timestamp: DateTimeOffset.UtcNow
    );
    _heavyweightEnvelope = new MessageEnvelope<HeavyweightCommand> {
      MessageId = MessageId.New(),
      Payload = heavyMessage,
      Hops = []
    };
    _heavyweightEnvelope.AddHop(new MessageHop {
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
      message: lightMessage,
      envelope: _lightweightEnvelope,
      services: serviceProvider,
      environment: "benchmark"
    );
  }

  [GlobalCleanup]
  public async Task Cleanup() {
    await _serialExecutor.StopAsync();
    await _parallelExecutor.StopAsync();
  }

  // ============================================================================
  // LIGHTWEIGHT COMMAND BENCHMARKS
  // ============================================================================

  [Benchmark(Baseline = true)]
  public async Task<int> Serial_LightweightCommand_SyncHandler() {
    return await _serialExecutor.ExecuteAsync<int>(
      _lightweightEnvelope,
      (env, ctx) => ValueTask.FromResult(((LightweightCommand)ctx.Message).Id),
      _context
    );
  }

  [Benchmark]
  public async Task<int> Parallel_LightweightCommand_SyncHandler() {
    return await _parallelExecutor.ExecuteAsync<int>(
      _lightweightEnvelope,
      (env, ctx) => ValueTask.FromResult(((LightweightCommand)ctx.Message).Id),
      _context
    );
  }

  [Benchmark]
  public async Task<int> Serial_LightweightCommand_AsyncHandler() {
    return await _serialExecutor.ExecuteAsync<int>(
      _lightweightEnvelope,
      async (env, ctx) => {
        await Task.Yield();
        return ((LightweightCommand)ctx.Message).Id;
      },
      _context
    );
  }

  [Benchmark]
  public async Task<int> Parallel_LightweightCommand_AsyncHandler() {
    return await _parallelExecutor.ExecuteAsync<int>(
      _lightweightEnvelope,
      async (env, ctx) => {
        await Task.Yield();
        return ((LightweightCommand)ctx.Message).Id;
      },
      _context
    );
  }

  // ============================================================================
  // HEAVYWEIGHT COMMAND BENCHMARKS
  // ============================================================================

  [Benchmark]
  public async Task<string> Serial_HeavyweightCommand_SyncHandler() {
    return await _serialExecutor.ExecuteAsync<string>(
      _heavyweightEnvelope,
      (env, ctx) => ValueTask.FromResult(((HeavyweightCommand)ctx.Message).Action),
      _context
    );
  }

  [Benchmark]
  public async Task<string> Parallel_HeavyweightCommand_SyncHandler() {
    return await _parallelExecutor.ExecuteAsync<string>(
      _heavyweightEnvelope,
      (env, ctx) => ValueTask.FromResult(((HeavyweightCommand)ctx.Message).Action),
      _context
    );
  }

  [Benchmark]
  public async Task<string> Serial_HeavyweightCommand_AsyncHandler() {
    return await _serialExecutor.ExecuteAsync<string>(
      _heavyweightEnvelope,
      async (env, ctx) => {
        await Task.Yield();
        var cmd = (HeavyweightCommand)ctx.Message;
        return $"{cmd.Action}_{cmd.Metadata["userId"]}";
      },
      _context
    );
  }

  [Benchmark]
  public async Task<string> Parallel_HeavyweightCommand_AsyncHandler() {
    return await _parallelExecutor.ExecuteAsync<string>(
      _heavyweightEnvelope,
      async (env, ctx) => {
        await Task.Yield();
        var cmd = (HeavyweightCommand)ctx.Message;
        return $"{cmd.Action}_{cmd.Metadata["userId"]}";
      },
      _context
    );
  }

  // ============================================================================
  // THROUGHPUT BENCHMARKS: 100 MESSAGES
  // ============================================================================

  [Benchmark]
  public async Task Serial_100Messages_LightweightCommands() {
    const int count = 100;
    var tasks = ArrayPool<Task<int>>.Shared.Rent(count);
    try {
      for (int i = 0; i < count; i++) {
        tasks[i] = _serialExecutor.ExecuteAsync<int>(
          _lightweightEnvelope,
          (env, ctx) => ValueTask.FromResult(i),
          _context
        ).AsTask();
      }
      await Task.WhenAll(tasks.AsSpan(0, count).ToArray());
    } finally {
      ArrayPool<Task<int>>.Shared.Return(tasks, clearArray: true);
    }
  }

  [Benchmark]
  public async Task Parallel_100Messages_LightweightCommands() {
    const int count = 100;
    var tasks = ArrayPool<Task<int>>.Shared.Rent(count);
    try {
      for (int i = 0; i < count; i++) {
        tasks[i] = _parallelExecutor.ExecuteAsync<int>(
          _lightweightEnvelope,
          (env, ctx) => ValueTask.FromResult(i),
          _context
        ).AsTask();
      }
      await Task.WhenAll(tasks.AsSpan(0, count).ToArray());
    } finally {
      ArrayPool<Task<int>>.Shared.Return(tasks, clearArray: true);
    }
  }

  [Benchmark]
  public async Task Serial_100Messages_HeavyweightCommands() {
    const int count = 100;
    var tasks = ArrayPool<Task<string>>.Shared.Rent(count);
    try {
      for (int i = 0; i < count; i++) {
        tasks[i] = _serialExecutor.ExecuteAsync<string>(
          _heavyweightEnvelope,
          (env, ctx) => ValueTask.FromResult($"result_{i}"),
          _context
        ).AsTask();
      }
      await Task.WhenAll(tasks.AsSpan(0, count).ToArray());
    } finally {
      ArrayPool<Task<string>>.Shared.Return(tasks, clearArray: true);
    }
  }

  [Benchmark]
  public async Task Parallel_100Messages_HeavyweightCommands() {
    const int count = 100;
    var tasks = ArrayPool<Task<string>>.Shared.Rent(count);
    try {
      for (int i = 0; i < count; i++) {
        tasks[i] = _parallelExecutor.ExecuteAsync<string>(
          _heavyweightEnvelope,
          (env, ctx) => ValueTask.FromResult($"result_{i}"),
          _context
        ).AsTask();
      }
      await Task.WhenAll(tasks.AsSpan(0, count).ToArray());
    } finally {
      ArrayPool<Task<string>>.Shared.Return(tasks, clearArray: true);
    }
  }

  // ============================================================================
  // REALISTIC SCENARIO BENCHMARKS
  // ============================================================================

  [Benchmark]
  public async Task RealisticScenario_Serial_OrderProcessing() {
    // Simulate realistic order processing with database lookups
    var tasks = new List<Task<OrderResult>>();
    for (int i = 0; i < 50; i++) {
      tasks.Add(_serialExecutor.ExecuteAsync<OrderResult>(
        _lightweightEnvelope,
        async (env, ctx) => {
          // Simulate database lookup
          await Task.Delay(1);

          return new OrderResult {
            OrderId = i,
            Status = "Completed",
            Total = i * 10.0m
          };
        },
        _context
      ).AsTask());
    }
    await Task.WhenAll(tasks);
  }

  [Benchmark]
  public async Task RealisticScenario_Parallel_OrderProcessing() {
    // Simulate realistic order processing with database lookups
    var tasks = new List<Task<OrderResult>>();
    for (int i = 0; i < 50; i++) {
      tasks.Add(_parallelExecutor.ExecuteAsync<OrderResult>(
        _lightweightEnvelope,
        async (env, ctx) => {
          // Simulate database lookup
          await Task.Delay(1);

          return new OrderResult {
            OrderId = i,
            Status = "Completed",
            Total = i * 10.0m
          };
        },
        _context
      ).AsTask());
    }
    await Task.WhenAll(tasks);
  }

  [Benchmark]
  public async Task RealisticScenario_Serial_EventSourcing() {
    // Simulate event sourcing pattern with synchronous completion
    var tasks = new List<Task<EventResult>>();
    for (int i = 0; i < 100; i++) {
      tasks.Add(_serialExecutor.ExecuteAsync<EventResult>(
        _lightweightEnvelope,
        (env, ctx) => ValueTask.FromResult(new EventResult {
          EventId = i,
          AggregateId = Guid.NewGuid(),
          Version = i,
          EventType = "OrderCreated"
        }),
        _context
      ).AsTask());
    }
    await Task.WhenAll(tasks);
  }

  [Benchmark]
  public async Task RealisticScenario_Parallel_EventSourcing() {
    // Simulate event sourcing pattern with synchronous completion
    var tasks = new List<Task<EventResult>>();
    for (int i = 0; i < 100; i++) {
      tasks.Add(_parallelExecutor.ExecuteAsync<EventResult>(
        _lightweightEnvelope,
        (env, ctx) => ValueTask.FromResult(new EventResult {
          EventId = i,
          AggregateId = Guid.NewGuid(),
          Version = i,
          EventType = "OrderCreated"
        }),
        _context
      ).AsTask());
    }
    await Task.WhenAll(tasks);
  }

  // ============================================================================
  // HELPER TYPES
  // ============================================================================

  private record OrderResult {
    public int OrderId { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal Total { get; init; }
  }

  private record EventResult {
    public int EventId { get; init; }
    public Guid AggregateId { get; init; }
    public int Version { get; init; }
    public string EventType { get; init; } = string.Empty;
  }
}
