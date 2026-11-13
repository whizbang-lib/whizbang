// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace ECommerce.Integration.Tests.Workflows;

/// <summary>
/// Tests for lifecycle receptor deserialization at Distribute stages.
/// These tests verify that messages stored as MessageEnvelope&lt;JsonElement&gt; can be correctly
/// deserialized back to their original types when lifecycle receptors are invoked.
/// This is the code path that was suspected of having a JsonElement bug.
/// </summary>
[Timeout(120_000)]
[NotInParallel("ServiceBus")]
public class LifecycleDeserializationTests {
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
    // CRITICAL: Use RunContinuationsAsynchronously to prevent deadlocks
    var completionSource = new TaskCompletionSource<ProductCreatedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receptor = new DistributeStageTestReceptor(completionSource);

    var registry = fixture.InventoryHost!.Services.GetRequiredService<IReceptorRegistry>();
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

      Console.WriteLine("[TEST SUCCESS] Event successfully deserialized at PostDistributeInline stage");
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
    // CRITICAL: Use RunContinuationsAsynchronously to prevent deadlocks
    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var expectedCount = commands.Length;
    var expectedProductIds = commands.Select(c => c.ProductId.Value).ToHashSet();  // Extract Guid from ProductId value object

    var receptor = new DistributeStageTestReceptor(new TaskCompletionSource<ProductCreatedEvent>(TaskCreationOptions.RunContinuationsAsynchronously));

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

    var registry = fixture.InventoryHost!.Services.GetRequiredService<IReceptorRegistry>();
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

  /// <summary>
  /// Every receptor the compiled registries route a ProductCreatedEvent to at PostDistributeInline must
  /// be one the inventory host can actually run. A routed entry nothing can resolve throws on every
  /// flush, and the outbox flush runs a stage for a whole batch, so one throw also skips the receptors
  /// of every other message coalesced into that batch. That made the multi-event test above lose an
  /// event whenever its two products' events were flushed together.
  /// </summary>
  [Test]
  public async Task CompiledPostDistributeInlineReceptors_ForProductCreatedEvent_AllRunInTheInventoryHostAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var registry = fixture.InventoryHost!.Services.GetRequiredService<IReceptorRegistry>();
    var evt = new ProductCreatedEvent {
      ProductId = Guid.CreateVersion7(),
      Name = "Routed receptor probe",
      Description = "Every compiled entry must resolve",
      Price = 1m
    };
    var envelope = new MessageEnvelope<ProductCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = evt,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    };

    var compiled = registry.GetReceptorsFor(typeof(ProductCreatedEvent), LifecycleStage.PostDistributeInline)
      .Where(r => r.RuntimeInstance is null)
      .ToList();
    await Assert.That(compiled).IsNotEmpty()
      .Because("the shared fixtures route DistributeStageTestReceptor here, so an empty list means the probe saw nothing");

    var failures = new List<string>();
    using var scope = fixture.InventoryHost.Services.CreateScope();
    foreach (var entry in compiled) {
      try {
        await entry.InvokeAsync(scope.ServiceProvider, evt, envelope, null, CancellationToken.None);
      } catch (InvalidOperationException ex) {
        failures.Add($"{entry.ReceptorId}: {ex.Message}");
      }
    }

    await Assert.That(failures).IsEmpty()
      .Because("a compiled route the host cannot resolve throws on every PostDistributeInline flush of the event");
  }

  // Helper receptor for counting multiple events
  [FireAt(LifecycleStage.PostDistributeInline)]
  // Constructed by hand so it can close over per-test state; the container has no way to supply
  // that callback. Declaring it keeps the receptor out of DI, where one un-constructible
  // descriptor would abort the whole service provider.
  [SuppressReceptorRegistration]
  public sealed class CustomDistributeReceptor(Action<ProductCreatedEvent> onReceived) : IReceptor<ProductCreatedEvent> {
    private readonly Action<ProductCreatedEvent> _onReceived = onReceived;

    public ValueTask HandleAsync(ProductCreatedEvent message, CancellationToken cancellationToken = default) {
      _onReceived(message);
      return ValueTask.CompletedTask;
    }
  }
}
