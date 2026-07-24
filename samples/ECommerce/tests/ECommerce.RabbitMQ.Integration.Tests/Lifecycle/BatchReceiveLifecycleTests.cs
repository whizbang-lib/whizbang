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
// Eventual-consistency under RabbitMQ — shared-infrastructure throughput on CI occasionally exceeds
// the per-test [Timeout] under parallel-module pressure. Matches the established [Retry(2)] convention
// on the sibling RabbitMQ workflow suites. Not a test-logic race.
[Retry(2)]
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

    var productId = ProductId.New();
    var command = new CreateProductCommand {
      ProductId = productId,
      Name = "RabbitMQ Batch Test",
      Description = "Testing batch receive end-to-end over RabbitMQ",
      Price = 29.99m,
      InitialStock = 3
    };

    // Act — dispatch, wait for 3 perspective events on inventory
    // (ProductCreated x2 + InventoryRestocked x1 from InitialStock > 0).
    // Deterministic helper: fails fast if no progress for 10s rather than waiting the full
    // ceiling; logs observed stream id + cross-stream count to distinguish flake from
    // genuine pipeline breakage.
    // Signal-based deterministic wait — IPerspectiveSyncAwaiter.WaitForStreamAsync()
    // returns when each perspective's cursor has caught up to all events that
    // SyncEventTracker recorded for this stream during dispatch. No event counts, no
    // progress watchdogs, no inter-event timing thresholds — the tracker holds the
    // pending event ids, the worker calls MarkProcessed when it applies, and the wait
    // task completes via TaskCompletionSource. The 90 s timeout is purely an upper bound
    // so a genuine pipeline stall surfaces; under normal CI load the wait returns
    // immediately once the cursor signals advance.
    await fixture.Dispatcher.SendAsync(command);
    await fixture.WaitForStreamCaughtUpAsync(
      streamId: productId.Value,
      perspectiveTypes: [
        typeof(ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective),
        typeof(ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective),
      ],
      timeout: TimeSpan.FromSeconds(90),
      hostFilter: "inventory");

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

    // Act — dispatch all three first, then wait per stream so the wait can
    // observe the full set of events the tracker recorded across all streams.
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }

    // Signal-based wait — one IPerspectiveSyncAwaiter wait per stream covers
    // both perspectives (InventoryLevels + ProductCatalog) per command, which
    // is what the old WaitForPerspectiveProcessingAsync(9) was approximating.
    // The 120s ceiling is upper-bound only; under normal CI load each wait
    // returns immediately once SyncEventTracker signals completion.
    var perspectiveTypes = new[] {
      typeof(ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective),
      typeof(ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective),
    };
    var waits = productIds.Select(id => fixture.WaitForStreamCaughtUpAsync(
      streamId: id.Value,
      perspectiveTypes: perspectiveTypes,
      timeout: TimeSpan.FromSeconds(120),
      hostFilter: "inventory"));
    await Task.WhenAll(waits);

    // Assert — all perspectives completed = all 3 messages batch-received and processed
  }
}
