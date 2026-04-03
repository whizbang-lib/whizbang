using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.InMemory.Integration.Tests.Fixtures;
using Medo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ECommerce.InMemory.Integration.Tests.Workflows;

/// <summary>
/// Integration test that reproduces the real BFF scenario:
/// Command → Event → Perspectives process → PostLifecycleInline → Tag hooks fire.
///
/// This is a RED/GREEN test:
/// - RED: Without the Phase 5 fix, PostLifecycleInline never fires, AuditTagHook never called
/// - GREEN: With the fix, the full pipeline works and the tag hook fires exactly once
/// </summary>
[NotInParallel("InMemory")]
[Timeout(60_000)]
public class TagHookLifecycleTests {
  private InMemoryIntegrationFixture? _fixture;
  private static readonly ProductId _testProductId = ProductId.From(Uuid7.NewUuid7().ToGuid());

  [Before(Test)]
  [RequiresUnreferencedCode("Test code")]
  [RequiresDynamicCode("Test code")]
  public async Task SetupAsync() {
    _fixture = await SharedInMemoryFixtureSource.GetFixtureAsync();
    await _fixture.CleanupDatabaseAsync();

    // Reset the static counter and signal before each test
    TagHookCallTracker.Reset();
  }

  /// <summary>
  /// LOCK-IN: Proves the full pipeline fires tag hooks at PostLifecycleInline
  /// after all perspectives complete for a ProductCreatedEvent.
  ///
  /// Flow: CreateProductCommand → ProductCreatedEvent → Perspectives → PostLifecycleInline → AuditTagHook
  ///
  /// If PostLifecycle doesn't fire (WhenAll broken), this test fails with count = 0.
  /// </summary>
  [Test]
  public async Task CreateProduct_PostLifecycleInline_AuditTagHookFires_Async() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Arrange
    var command = new CreateProductCommand {
      ProductId = _testProductId,
      Name = "Tag Hook Test Product",
      Description = "Tests that tag hooks fire at PostLifecycleInline",
      Price = 49.99m,
      ImageUrl = "/images/tag-test.png",
      InitialStock = 10
    };

    // Act — dispatch command, wait for ALL perspectives to complete
    using var productWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);

    await fixture.Dispatcher.SendAsync(command);
    await productWaiter.WaitAsync(timeoutMilliseconds: 15000);
    await restockWaiter.WaitAsync(timeoutMilliseconds: 15000);

    // Wait for PostLifecycleInline tag hook to fire using completion signal (not Task.Delay)
    await TagHookCallTracker.WaitForPostLifecycleInlineAsync(TimeSpan.FromSeconds(15));

    // Assert — AuditTagHook MUST have fired at PostLifecycleInline
    // ProductCreatedEvent has [AuditEvent] attribute, and AuditTagHook is registered
    // at PostLifecycleInline in the fixture's BFF host.
    await Assert.That(TagHookCallTracker.PostLifecycleInlineCount).IsGreaterThanOrEqualTo(1)
      .Because("LOCK-IN: AuditTagHook must fire at PostLifecycleInline after all perspectives complete. " +
               "If this fails, the PostLifecycle WhenAll pipeline is broken.");
  }
}

/// <summary>
/// Static tracker for tag hook invocations across DI scopes.
/// Used by integration tests to verify tag hooks fire correctly.
/// Uses a completion signal so tests can wait deterministically instead of polling.
/// </summary>
public static class TagHookCallTracker {
  private static int _postLifecycleInlineCount;
  private static TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public static int PostLifecycleInlineCount => _postLifecycleInlineCount;

  public static void RecordPostLifecycleInline() {
    Interlocked.Increment(ref _postLifecycleInlineCount);
    _signal.TrySetResult();
  }

  /// <summary>
  /// Waits for at least one PostLifecycleInline call using a completion signal.
  /// </summary>
  public static async Task WaitForPostLifecycleInlineAsync(TimeSpan timeout) {
    using var cts = new CancellationTokenSource(timeout);
    await _signal.Task.WaitAsync(cts.Token);
  }

  public static void Reset() {
    Interlocked.Exchange(ref _postLifecycleInlineCount, 0);
    _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  }
}
