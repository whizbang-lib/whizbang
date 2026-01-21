using System;
using System.Linq;
using System.Threading.Tasks;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace ECommerce.Integration.Tests.Workflows;

/// <summary>
/// Tests for lifecycle receptor deserialization at Distribute stages.
/// These tests verify that messages stored as MessageEnvelope&lt;JsonElement&gt; can be correctly
/// deserialized back to their original types when lifecycle receptors are invoked.
/// This is the code path that was suspected of having a JsonElement bug.
/// </summary>
[Timeout(30_000)]  // 30s timeout per test
[NotInParallel]
public class LifecycleDeserializationTests {
  private static ServiceBusIntegrationFixture? _fixture;

  [Before(Test)]
  public async Task SetupAsync() {
    // Get shared ServiceBus resources
    var (connectionString, sharedClient) = await SharedFixtureSource.GetSharedResourcesAsync(0);

    // Create new fixture with shared ServiceBus client
    _fixture = new ServiceBusIntegrationFixture(connectionString, sharedClient, batchIndex: 0);
    await _fixture.InitializeAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_fixture != null) {
      await _fixture.DisposeAsync();
      _fixture = null;
    }
  }

  /// <summary>
  /// Tests that ProductCreatedEvent can be deserialized at PostDistributeInline stage.
  /// This exercises the JsonLifecycleMessageDeserializer.DeserializeFromEnvelope() code path.
  /// </summary>
  [Test]
  public async Task ProductCreatedEvent_DeserializedAtDistributeStage_SuccessfullyAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Lifecycle Test Product",
      Description = "Testing deserialization at Distribute stage",
      Price = 123.45m,
      ImageUrl = "/images/test.png",
      InitialStock = 50
    };

    // Register a receptor at PostDistributeInline stage to capture the deserialized event
    var completionSource = new TaskCompletionSource<ProductCreatedEvent>();
    var receptor = new DistributeStageTestReceptor(completionSource);

    var registry = fixture.InventoryHost!.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostDistributeInline);

    try {
      // Act - Send command (this will trigger ProductCreatedEvent at Distribute stage)
      await fixture.Dispatcher.SendAsync(command);

      // Wait for the receptor to receive the deserialized event
      var receivedEvent = await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(15));

      // Assert - Verify the event was correctly deserialized
      await Assert.That(receivedEvent).IsNotNull();
      await Assert.That(receivedEvent.ProductId).IsEqualTo(command.ProductId);
      await Assert.That(receivedEvent.Name).IsEqualTo(command.Name);
      await Assert.That(receivedEvent.Description).IsEqualTo(command.Description);
      await Assert.That(receivedEvent.Price).IsEqualTo(command.Price);

      Console.WriteLine($"[TEST SUCCESS] Event successfully deserialized at PostDistributeInline stage");
    } finally {
      // Cleanup
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostDistributeInline);
    }
  }

  /// <summary>
  /// Tests multiple events being deserialized at Distribute stage.
  /// This ensures the deserialization works consistently across multiple messages.
  /// </summary>
  [Test]
  public async Task MultipleEvents_DeserializedAtDistributeStage_AllSucceedAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var commands = new[] {
      new CreateProductCommand {
        ProductId = ProductId.New(),
        Name = "Product 1",
        Description = "First test product",
        Price = 10.00m,
        ImageUrl = "/images/product1.png",
        InitialStock = 100
      },
      new CreateProductCommand {
        ProductId = ProductId.New(),
        Name = "Product 2",
        Description = "Second test product",
        Price = 20.00m,
        ImageUrl = "/images/product2.png",
        InitialStock = 200
      }
    };

    // Use ConcurrentDictionary to deduplicate by ProductId (Service Bus has at-least-once delivery)
    var receivedEvents = new System.Collections.Concurrent.ConcurrentDictionary<Guid, ProductCreatedEvent>();
    var completionSource = new TaskCompletionSource<bool>();
    var expectedCount = commands.Length;
    var expectedProductIds = commands.Select(c => c.ProductId.Value).ToHashSet();  // Extract Guid from ProductId value object

    var receptor = new DistributeStageTestReceptor(new TaskCompletionSource<ProductCreatedEvent>());

    // Create a custom receptor that counts events ONLY for products sent in THIS test
    // This prevents counting stale events from previous tests or concurrent processes
    // Deduplicates by ProductId since Service Bus may deliver same message multiple times
    var countingReceptor = new CustomDistributeReceptor((evt) => {
      if (expectedProductIds.Contains(evt.ProductId)) {
        // TryAdd returns true only for first occurrence - deduplicates retries
        if (receivedEvents.TryAdd(evt.ProductId, evt)) {
          if (receivedEvents.Count >= expectedCount) {
            completionSource.TrySetResult(true);
          }
        } else {
          Console.WriteLine($"[TEST] Duplicate event received for ProductId={evt.ProductId} (Service Bus retry)");
        }
      } else {
        Console.WriteLine($"[TEST] Ignoring event for ProductId={evt.ProductId} (not in this test's expected set)");
      }
    });

    var registry = fixture.InventoryHost!.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(countingReceptor, LifecycleStage.PostDistributeInline);

    try {
      // Act - Send all commands
      foreach (var command in commands) {
        await fixture.Dispatcher.SendAsync(command);
      }

      // Wait for all events to be received
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(20));

      // Assert - Verify all events were deserialized (unique by ProductId)
      await Assert.That(receivedEvents.Count).IsEqualTo(expectedCount);

      foreach (var command in commands) {
        var hasEvent = receivedEvents.TryGetValue(command.ProductId.Value, out var matchingEvent);
        await Assert.That(hasEvent).IsTrue();
        await Assert.That(matchingEvent!.Name).IsEqualTo(command.Name);
      }

      Console.WriteLine($"[TEST SUCCESS] All {expectedCount} unique events successfully deserialized");
    } finally {
      // Cleanup
      registry.Unregister<ProductCreatedEvent>(countingReceptor, LifecycleStage.PostDistributeInline);
    }
  }

  // Helper receptor for counting multiple events
  [FireAt(LifecycleStage.PostDistributeInline)]
  public sealed class CustomDistributeReceptor : IReceptor<ProductCreatedEvent> {
    private readonly Action<ProductCreatedEvent> _onReceived;

    public CustomDistributeReceptor(Action<ProductCreatedEvent> onReceived) {
      _onReceived = onReceived;
    }

    public ValueTask HandleAsync(ProductCreatedEvent message, CancellationToken cancellationToken = default) {
      _onReceived(message);
      return ValueTask.CompletedTask;
    }
  }
}
