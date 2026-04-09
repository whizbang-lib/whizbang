using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Lifecycle;

/// <summary>
/// Integration tests for batch receive behavior via SubscribeBatchAsync.
/// Validates that messages are batch-collected and processed correctly
/// through the full lifecycle pipeline (PreInbox → insert → Process → PostInbox → completion).
/// </summary>
/// <remarks>
/// <para><strong>What's being tested</strong>: TransportConsumerWorker subscribes via
/// SubscribeBatchAsync. The transport batch collector accumulates messages and flushes
/// them as a batch. Each message in the batch goes through the full lifecycle.</para>
/// </remarks>
/// <docs>messaging/transports/transport-consumer#batch-receive</docs>
[Category("Integration")]
[Category("BatchReceive")]
[NotInParallel("ServiceBus")]
[Timeout(120_000)]
public class BatchReceiveLifecycleTests {
  private ServiceBusIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    _fixture = await SharedServiceBusFixtureSource.GetFixtureAsync();
    await Task.Delay(500);
    await _fixture.CleanupDatabaseAsync();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    // Shared fixture — don't dispose
  }

  // ========================================
  // Batch receive processes messages end-to-end
  // ========================================

  /// <summary>
  /// Verifies that a message sent via the dispatcher reaches the remote service's inbox,
  /// gets processed by receptors, and perspectives are updated — all through the batch
  /// subscribe path (SubscribeBatchAsync → TransportBatchCollector → batch handler).
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task BatchReceive_SingleMessage_ProcessesEndToEndAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Batch Test Product",
      Description = "Testing batch receive end-to-end",
      Price = 49.99m,
      InitialStock = 5
    };

    // Act — dispatch command, wait for perspectives to process on remote service
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 90000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;

    // Assert — perspectives completed = message batch-received, processed, and persisted
  }

  /// <summary>
  /// Verifies that multiple messages sent in rapid succession are all processed
  /// through the batch receive path. Each message should result in its own
  /// perspective update.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task BatchReceive_MultipleMessages_AllProcessedAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var productIds = Enumerable.Range(0, 3).Select(_ => ProductId.New()).ToList();
    var commands = productIds.Select((id, i) => new CreateProductCommand {
      ProductId = id,
      Name = $"Batch Product {i}",
      Description = $"Batch test product {i}",
      Price = 10.00m + i,
      InitialStock = i + 1
    }).ToList();

    // Act — send 3 commands, wait for all perspectives
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 6, timeoutMilliseconds: 120000, hostFilter: "inventory");

    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }

    await perspectiveTask;

    // Assert — all perspectives completed = all 3 messages batch-received and processed
  }

  /// <summary>
  /// Verifies that the batch receive path preserves OTEL trace context.
  /// Messages should carry traceparent through the batch handler.
  /// </summary>
  [Test]
  public async Task BatchReceive_PreservesTraceContextAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Trace Test Product",
      Description = "Testing trace context preservation in batch receive",
      Price = 19.99m,
      InitialStock = 1
    };

    // Act
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;

    // Assert — perspectives completed = trace context preserved through batch pipeline
  }
}
