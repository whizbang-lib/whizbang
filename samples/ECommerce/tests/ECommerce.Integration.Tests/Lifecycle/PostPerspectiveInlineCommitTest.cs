using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Contracts.Lenses;
using ECommerce.Integration.Tests.Fixtures;
using Medo;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace ECommerce.Integration.Tests.Lifecycle;

/// <summary>
/// Minimal reproduction test: PostPerspectiveInline MUST fire AFTER database transaction commits.
/// </summary>
[NotInParallel("ServiceBus")]
public class PostPerspectiveInlineCommitTest {
  private static ServiceBusIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    var testIndex = 10;
    var (connectionString, sharedClient) = await SharedFixtureSource.GetSharedResourcesAsync(testIndex);
    _fixture = new ServiceBusIntegrationFixture(connectionString, sharedClient, 0);
    await _fixture.InitializeAsync();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_fixture != null) {
      try {
        await _fixture.CleanupDatabaseAsync();
      } catch (Exception ex) {
        Console.WriteLine($"[After(Test)] Warning: Cleanup encountered error (non-critical): {ex.Message}");
      }
      await _fixture.DisposeAsync();
      _fixture = null;
    }
  }

  /// <summary>
  /// CRITICAL BUG REPRODUCTION:
  /// PostPerspectiveInline fires BEFORE database transaction commits,
  /// so perspective data is NOT queryable when the receptor fires.
  ///
  /// Expected: receptor fires → query succeeds → data is committed
  /// Actual: receptor fires → query returns null → data NOT committed yet
  /// </summary>
  [Test]
  [Timeout(120000)]  // Increased to 2 minutes
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

    var completionSource = new TaskCompletionSource<bool>();
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      completionSource,
      perspectiveName: "ProductCatalogPerspective");
    Console.WriteLine("[TEST] Created GenericLifecycleCompletionReceptor for ProductCatalog perspective");

    Console.WriteLine("[TEST] Getting ILifecycleReceptorRegistry from BffHost...");
    var registry = fixture.BffHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    Console.WriteLine($"[TEST] Got registry: {registry.GetType().Name}");

    Console.WriteLine("[TEST] Registering receptor for PostPerspectiveInline stage...");
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);
    Console.WriteLine("[TEST] Receptor registered successfully");

    try {
      // Act - Dispatch command
      Console.WriteLine("----------------------------------------------------------");
      Console.WriteLine($"[TEST] >>> DISPATCHING CreateProductCommand for ProductId={productId}");
      Console.WriteLine("----------------------------------------------------------");
      await fixture.Dispatcher.SendAsync(command);
      Console.WriteLine("[TEST] Dispatcher.SendAsync() returned");

      // DIAGNOSTIC: Wait for event to be processed
      Console.WriteLine("[TEST] Waiting 5s for event processing...");
      for (int i = 1; i <= 5; i++) {
        await Task.Delay(1000);
        Console.WriteLine($"[TEST] ... {i}s elapsed, receptor invocation count: {receptor.InvocationCount}");
      }

      // Wait for PostPerspectiveInline to fire (this is the BLOCKING stage)
      Console.WriteLine("----------------------------------------------------------");
      Console.WriteLine("[TEST] >>> WAITING for PostPerspectiveInline to fire...");
      Console.WriteLine("[TEST] Timeout: 30 seconds");
      Console.WriteLine("----------------------------------------------------------");
      try {
        await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Console.WriteLine("**********************************************************");
        Console.WriteLine($"[TEST] ✓✓✓ PostPerspectiveInline FIRED! Receptor invocation count: {receptor.InvocationCount}");
        Console.WriteLine("**********************************************************");
      } catch (TimeoutException) {
        Console.WriteLine("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        Console.WriteLine("[TEST] ✗✗✗ TIMEOUT waiting for PostPerspectiveInline!");
        Console.WriteLine($"[TEST] Receptor invocation count: {receptor.InvocationCount}");
        Console.WriteLine("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        throw;
      }

      // Assert - Receptor is invoked only for ProductCatalogPerspective (filtered by perspectiveName)
      // InventoryLevelsPerspective also processes ProductCreatedEvent but is filtered out by the receptor
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

      // Assert - CRITICAL: Data MUST be queryable immediately after PostPerspectiveInline fires
      // This is the ENTIRE PURPOSE of PostPerspectiveInline - it's a blocking stage that
      // guarantees the transaction has committed and data is persisted.

      Console.WriteLine("----------------------------------------------------------");
      Console.WriteLine("[TEST] >>> QUERYING for perspective data...");
      Console.WriteLine($"[TEST] Looking for ProductId: {productId}");
      Console.WriteLine("----------------------------------------------------------");

      // DIAGNOSTIC: Try with retry to see if it's a timing issue
      ProductDto? product = null;
      var retryCount = 10;
      for (int i = 0; i < retryCount; i++) {
        Console.WriteLine($"[TEST] Query attempt {i + 1}/{retryCount}...");
        product = await fixture.BffProductLens.GetByIdAsync(productId);
        if (product != null) {
          Console.WriteLine($"[TEST] ✓✓✓ Product FOUND on attempt {i + 1}!");
          Console.WriteLine($"[TEST]   - Name: {product.Name}");
          Console.WriteLine($"[TEST]   - Price: {product.Price}");
          break;
        }
        Console.WriteLine($"[TEST] ✗ Product NOT found on attempt {i + 1}");
        if (i < retryCount - 1) {
          Console.WriteLine($"[TEST] Retrying after 200ms delay...");
          await Task.Delay(200);
        }
      }

      // THIS IS THE BUG: product is null because transaction hasn't committed yet!
      Console.WriteLine("----------------------------------------------------------");
      Console.WriteLine("[TEST] >>> ASSERTING product is not null...");
      Console.WriteLine("----------------------------------------------------------");
      await Assert.That(product).IsNotNull();

      Console.WriteLine("[TEST] >>> ASSERTING product properties...");
      await Assert.That(product!.Name).IsEqualTo(command.Name);
      await Assert.That(product.Price).IsEqualTo(command.Price);

      Console.WriteLine("==========================================================");
      Console.WriteLine($"[TEST] ✓✓✓ SUCCESS! Product found: {product.Name}");
      Console.WriteLine("==========================================================");

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);
    }
  }
}
