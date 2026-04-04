using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;
using Medo;

namespace ECommerce.Integration.Tests.Lifecycle;

/// <summary>
/// Minimal reproduction test: PostPerspectiveInline MUST fire AFTER database transaction commits.
/// </summary>
[NotInParallel("ServiceBus")]
[Timeout(120_000)]
public class PostPerspectiveInlineCommitTest {
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
  public async Task TeardownAsync() {
    // Don't dispose - shared fixture is reused across tests
  }

  /// <summary>
  /// CRITICAL BUG REPRODUCTION:
  /// PostPerspectiveInline fires BEFORE database transaction commits,
  /// so perspective data is NOT queryable when the receptor fires.
  ///
  /// Expected: receptor fires -> query succeeds -> data is committed
  /// Actual: receptor fires -> query returns null -> data NOT committed yet
  /// </summary>
  [Test]
  public async Task PostPerspectiveInline_MustFireAfterTransactionCommits_DataMustBeQueryableAsync() {
    Console.WriteLine("==========================================================");
    Console.WriteLine("[TEST] ===== POST PERSPECTIVE INLINE COMMIT TEST START =====");
    Console.WriteLine("==========================================================");

    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var productId = ProductId.From(Uuid7.NewUuid7().ToGuid());
    Console.WriteLine($"[TEST] Generated ProductId: {productId}");

    var command = new CreateProductCommand {
      ProductId = productId,
      Name = "Test Product",
      Description = "This test verifies PostPerspectiveInline fires AFTER transaction commits",
      Price = 99.99m,
      InitialStock = 10
    };
    Console.WriteLine($"[TEST] Created command: Name={command.Name}, Price={command.Price}, InitialStock={command.InitialStock}");

    // Act - Use hook to wait for perspective processing
    Console.WriteLine("[TEST] Setting up perspective processing hook...");
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");

    Console.WriteLine("----------------------------------------------------------");
    Console.WriteLine($"[TEST] >>> DISPATCHING CreateProductCommand for ProductId={productId}");
    Console.WriteLine("----------------------------------------------------------");
    await fixture.Dispatcher.SendAsync(command);
    Console.WriteLine("[TEST] Dispatcher.SendAsync() returned");

    Console.WriteLine("----------------------------------------------------------");
    Console.WriteLine("[TEST] >>> WAITING for perspective processing to complete...");
    Console.WriteLine("----------------------------------------------------------");
    await perspectiveTask;
    Console.WriteLine("[TEST] Perspective processing completed");

    // Wait for workers to be idle before querying data
    await fixture.WaitForWorkersIdleAsync();

    // Assert - Perspective data should be queryable after PostPerspectiveInline
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(command.ProductId);
    await Assert.That(inventoryProduct).IsNotNull();

    Console.WriteLine("==========================================================");
    Console.WriteLine("[TEST] PostPerspectiveInline fired successfully, data is queryable");
    Console.WriteLine("==========================================================");
  }
}
