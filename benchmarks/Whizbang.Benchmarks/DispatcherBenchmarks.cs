using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Benchmarks for Dispatcher message routing and orchestration.
/// Compares performance of LocalInvokeAsync (in-process, zero allocation)
/// vs SendAsync (returns delivery receipt).
/// TARGET: LocalInvoke < 20ns, 0 bytes allocation
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class DispatcherBenchmarks {
  // Test messages
  public record LightweightCommand(int Value);
  public record LightweightResult(int Value);

  public record HeavyweightCommand(string Data, Guid[] Ids, Dictionary<string, object> Metadata);
  public record HeavyweightResult(string Summary, int Count);

  private IDispatcher _dispatcher = null!;
  private IDispatcher _dispatcherWithTracing = null!;
  private IMessageContext _context = null!;
  private LightweightCommand _lightCommand = null!;
  private HeavyweightCommand _heavyCommand = null!;

  [GlobalSetup]
  public void Setup() {
    // Dispatcher without tracing
    var services = new ServiceCollection();
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    _dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    // Dispatcher with tracing
    var servicesWithTracing = new ServiceCollection();
    servicesWithTracing.AddReceptors();
    servicesWithTracing.AddSingleton<ITraceStore>(new InMemoryTraceStore());
    servicesWithTracing.AddWhizbangDispatcher();
    var serviceProviderWithTracing = servicesWithTracing.BuildServiceProvider();
    _dispatcherWithTracing = serviceProviderWithTracing.GetRequiredService<IDispatcher>();

    // Pre-create context
    _context = MessageContext.New();

    // Pre-create test messages
    _lightCommand = new LightweightCommand(42);
    _heavyCommand = new HeavyweightCommand(
      Data: new string('x', 1000),
      Ids: [.. Enumerable.Range(0, 10).Select(_ => Guid.NewGuid())],
      Metadata: new Dictionary<string, object> {
                { "key1", "value1" },
                { "key2", 123 },
                { "key3", true }
            }
    );
  }

  // ========================================
  // LOCAL INVOKE PATTERN - In-Process, Zero Allocation
  // TARGET: < 20ns, 0 bytes
  // ========================================

  [Benchmark(Baseline = true)]
  public async Task<LightweightResult> LocalInvoke_Lightweight_NoTracing() {
    return await _dispatcher.LocalInvokeAsync<LightweightResult>(_lightCommand);
  }

  [Benchmark]
  public async Task<LightweightResult> LocalInvoke_Lightweight_WithTracing() {
    return await _dispatcherWithTracing.LocalInvokeAsync<LightweightResult>(_lightCommand);
  }

  [Benchmark]
  public async Task<LightweightResult> LocalInvoke_Lightweight_WithContext() {
    return await _dispatcher.LocalInvokeAsync<LightweightResult>(_lightCommand, _context);
  }

  [Benchmark]
  public async Task<HeavyweightResult> LocalInvoke_Heavyweight_NoTracing() {
    return await _dispatcher.LocalInvokeAsync<HeavyweightResult>(_heavyCommand);
  }

  [Benchmark]
  public async Task<HeavyweightResult> LocalInvoke_Heavyweight_WithTracing() {
    return await _dispatcherWithTracing.LocalInvokeAsync<HeavyweightResult>(_heavyCommand);
  }

  // ========================================
  // SEND PATTERN - Returns Delivery Receipt
  // ========================================

  [Benchmark]
  public async Task<IDeliveryReceipt> Send_Lightweight_NoTracing() {
    return await _dispatcher.SendAsync(_lightCommand);
  }

  [Benchmark]
  public async Task<IDeliveryReceipt> Send_Lightweight_WithTracing() {
    return await _dispatcherWithTracing.SendAsync(_lightCommand);
  }

  [Benchmark]
  public async Task<IDeliveryReceipt> Send_Lightweight_WithContext() {
    return await _dispatcher.SendAsync(_lightCommand, _context);
  }

  // ========================================
  // BATCH OPERATIONS
  // ========================================

  [Benchmark]
  public async Task LocalInvoke_100Commands_NoTracing() {
    const int count = 100;
    var tasks = new Task<LightweightResult>[count];
    for (int i = 0; i < count; i++) {
      var cmd = new LightweightCommand(i);
      tasks[i] = _dispatcher.LocalInvokeAsync<LightweightResult>(cmd).AsTask();
    }
    await Task.WhenAll(tasks);
  }

  [Benchmark]
  public async Task LocalInvoke_100Commands_WithTracing() {
    const int count = 100;
    var tasks = new Task<LightweightResult>[count];
    for (int i = 0; i < count; i++) {
      var cmd = new LightweightCommand(i);
      tasks[i] = _dispatcherWithTracing.LocalInvokeAsync<LightweightResult>(cmd).AsTask();
    }
    await Task.WhenAll(tasks);
  }

  [Benchmark]
  public async Task LocalInvokeMany_100Commands() {
    var commands = Enumerable.Range(0, 100)
        .Select(i => (object)new LightweightCommand(i))
        .ToList();
    await _dispatcher.LocalInvokeManyAsync<LightweightResult>(commands);
  }

  [Benchmark]
  public async Task Send_100Commands_NoTracing() {
    const int count = 100;
    var tasks = new Task<IDeliveryReceipt>[count];
    for (int i = 0; i < count; i++) {
      var cmd = new LightweightCommand(i);
      tasks[i] = _dispatcher.SendAsync(cmd);
    }
    await Task.WhenAll(tasks);
  }

  [Benchmark]
  public async Task SendMany_100Commands() {
    var commands = Enumerable.Range(0, 100)
        .Select(i => (object)new LightweightCommand(i))
        .ToList();
    await _dispatcher.SendManyAsync(commands);
  }

  // ========================================
  // COLD START VS HOT PATH
  // ========================================

  [Benchmark]
  public static async Task ColdStart_FirstLocalInvoke() {
    // Simulate cold start by creating fresh dispatcher
    var services = new ServiceCollection();
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    var freshDispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var cmd = new LightweightCommand(1);
    await freshDispatcher.LocalInvokeAsync<LightweightResult>(cmd);
  }

  [Benchmark]
  public async Task HotPath_RepeatedLocalInvoke() {
    // Use pre-warmed dispatcher
    var cmd = new LightweightCommand(1);
    await _dispatcher.LocalInvokeAsync<LightweightResult>(cmd);
  }

  // ========================================
  // REALISTIC SCENARIOS
  // ========================================

  [Benchmark]
  public async Task RealisticScenario_OrderProcessing_LocalInvoke() {
    var orderCommand = new ProcessOrderCommand(
      OrderId: Guid.NewGuid(),
      CustomerId: Guid.NewGuid(),
      Items: ["Item1", "Item2", "Item3"],
      TotalAmount: 99.99m
    );

    await _dispatcher.LocalInvokeAsync<OrderProcessingResult>(orderCommand);
  }

  [Benchmark]
  public async Task RealisticScenario_OrderProcessing_Send() {
    var orderCommand = new ProcessOrderCommand(
      OrderId: Guid.NewGuid(),
      CustomerId: Guid.NewGuid(),
      Items: ["Item1", "Item2", "Item3"],
      TotalAmount: 99.99m
    );

    await _dispatcher.SendAsync(orderCommand);
  }

  [Benchmark]
  public async Task RealisticScenario_EventSourcing_LocalInvoke() {
    var eventCommand = new RecordEventCommand(
      StreamId: Guid.NewGuid().ToString(),
      EventType: "OrderPlaced",
      Data: new Dictionary<string, object> {
                { "orderId", Guid.NewGuid() },
                { "timestamp", DateTimeOffset.UtcNow },
                { "amount", 100.00 }
            }
    );

    await _dispatcher.LocalInvokeAsync<EventRecordingResult>(eventCommand);
  }

  // ========================================
  // SUPPORTING TYPES
  // ========================================

  // Realistic command types for scenarios
  public record ProcessOrderCommand(
    Guid OrderId,
    Guid CustomerId,
    string[] Items,
    decimal TotalAmount
  );

  public record OrderProcessingResult(
    Guid OrderId,
    string Status,
    DateTimeOffset ProcessedAt
  );

  public record RecordEventCommand(
    string StreamId,
    string EventType,
    Dictionary<string, object> Data
  );

  public record EventRecordingResult(
    string StreamId,
    long SequenceNumber,
    DateTimeOffset RecordedAt
  );

  // ========================================
  // TEST RECEPTORS
  // ========================================

  public class LightweightReceptor : IReceptor<LightweightCommand, LightweightResult> {
    public ValueTask<LightweightResult> HandleAsync(
      LightweightCommand message,
      CancellationToken cancellationToken = default
    ) {
      // Synchronous completion (zero allocation with ValueTask)
      return ValueTask.FromResult(new LightweightResult(message.Value * 2));
    }
  }

  public class HeavyweightReceptor : IReceptor<HeavyweightCommand, HeavyweightResult> {
    public async ValueTask<HeavyweightResult> HandleAsync(
      HeavyweightCommand message,
      CancellationToken cancellationToken = default
    ) {
      // Simulate some async work
      await Task.Yield();

      return new HeavyweightResult(
        Summary: $"Processed {message.Data.Length} bytes with {message.Ids.Length} IDs",
        Count: message.Metadata.Count
      );
    }
  }

  public class OrderProcessingReceptor : IReceptor<ProcessOrderCommand, OrderProcessingResult> {
    public async ValueTask<OrderProcessingResult> HandleAsync(
      ProcessOrderCommand message,
      CancellationToken cancellationToken = default
    ) {
      // Simulate order processing logic
      await Task.Delay(1, cancellationToken);

      return new OrderProcessingResult(
        OrderId: message.OrderId,
        Status: "Processed",
        ProcessedAt: DateTimeOffset.UtcNow
      );
    }
  }

  public class EventRecordingReceptor : IReceptor<RecordEventCommand, EventRecordingResult> {
    private static long _sequence = 0;

    public async ValueTask<EventRecordingResult> HandleAsync(
      RecordEventCommand message,
      CancellationToken cancellationToken = default
    ) {
      // Simulate event storage
      await Task.Yield();

      return new EventRecordingResult(
        StreamId: message.StreamId,
        SequenceNumber: Interlocked.Increment(ref _sequence),
        RecordedAt: DateTimeOffset.UtcNow
      );
    }
  }

  // Simple in-memory trace store for benchmarking
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

    public Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset startTime, DateTimeOffset endTime, CancellationToken cancellationToken = default) {
      return Task.FromResult(new List<IMessageEnvelope>());
    }
  }
}
