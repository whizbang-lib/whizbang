using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;
using ECommerce.RabbitMQ.Integration.Tests.Fixtures;

namespace ECommerce.RabbitMQ.Integration.Tests.Lifecycle;

/// <summary>
/// Integration tests for batch receive behavior via SubscribeBatchAsync over RabbitMQ.
/// Validates that messages are batch-collected and processed correctly through the full
/// lifecycle pipeline (PreInbox → insert → Process → PostInbox → completion).
/// </summary>
/// <remarks>
/// <para><strong>What's being tested</strong>: TransportConsumerWorker subscribes via
/// SubscribeBatchAsync. The RabbitMQ transport collects messages via TransportBatchCollector,
/// flushes them as a batch, and ACKs each message individually (multiple=false) after
/// the batch handler succeeds.</para>
/// <para><strong>Key RabbitMQ-specific behaviors</strong>:</para>
/// <list type="bullet">
///   <item>ReceivedAsync returns Task.CompletedTask (non-blocking, bypasses ConsumerDispatchConcurrency=1)</item>
///   <item>Per-message BasicAckAsync (NOT multi-ACK — avoids PRECONDITION_FAILED with AutorecoveringChannel)</item>
///   <item>Handler failure → per-message BasicNackAsync with requeue</item>
/// </list>
/// </remarks>
/// <docs>messaging/transports/rabbitmq#batch-receive</docs>
[Category("Integration")]
[Category("BatchReceive")]
[NotInParallel("RabbitMQ")]
public class BatchReceiveLifecycleTests {
  private static RabbitMqIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync(CancellationToken cancellationToken) {
    _fixture = await SharedRabbitMqFixtureSource.GetFixtureAsync();
    await _fixture.CleanupDatabaseAsync();
  }

  [After(Test)]
  public Task CleanupAsync(CancellationToken cancellationToken) {
    return Task.CompletedTask;
  }

  // ========================================
  // End-to-end batch receive
  // ========================================

  /// <summary>
  /// Verifies that a message dispatched via outbox → RabbitMQ → inbox
  /// is received, processed, and perspectives updated — all through the
  /// batch subscribe path.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task BatchReceive_SingleMessage_ProcessesEndToEndAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "RabbitMQ Batch Test",
      Description = "Testing batch receive end-to-end over RabbitMQ",
      Price = 29.99m,
      InitialStock = 3
    };

    // Act — dispatch, wait for perspectives on remote service
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 90000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;

    // Assert — perspectives completed = message was received via batch, processed, and persisted
    // If perspectives didn't fire, WaitForPerspectiveProcessingAsync would have timed out
  }

  /// <summary>
  /// Verifies that multiple messages sent rapidly are all processed correctly.
  /// The batch collector should accumulate them and flush as a batch.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task BatchReceive_MultipleMessages_AllProcessedAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var productIds = Enumerable.Range(0, 3).Select(_ => ProductId.New()).ToList();
    var commands = productIds.Select((id, i) => new CreateProductCommand {
      ProductId = id,
      Name = $"RabbitMQ Batch Product {i}",
      Description = $"Batch test product {i}",
      Price = 10.00m + i,
      InitialStock = i + 1
    }).ToList();

    // Act — send 3 commands with brief gaps to avoid overwhelming CI runners
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 6, timeoutMilliseconds: 120000, hostFilter: "inventory");

    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }

    await perspectiveTask;

    // Assert — all perspectives completed = all 3 messages batch-received and processed
  }
}
