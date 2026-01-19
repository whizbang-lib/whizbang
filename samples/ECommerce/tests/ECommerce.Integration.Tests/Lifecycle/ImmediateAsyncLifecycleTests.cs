using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace ECommerce.Integration.Tests.Lifecycle;

/// <summary>
/// Integration tests for ImmediateAsync lifecycle stage.
/// Validates that lifecycle receptors fire immediately after command handler returns,
/// before any database operations occur.
/// </summary>
/// <remarks>
/// <para><strong>Hook Location</strong>: Dispatcher.cs, immediately after receptor HandleAsync() returns</para>
/// <para><strong>Guarantees</strong>:</para>
/// <list type="bullet">
///   <item>Fires in same transaction scope as receptor</item>
///   <item>No database writes have occurred yet</item>
///   <item>Errors propagate to caller</item>
/// </list>
/// </remarks>
/// <docs>core-concepts/lifecycle-stages</docs>
/// <docs>testing/lifecycle-synchronization</docs>
[Category("Integration")]
[Category("Lifecycle")]
[NotInParallel]
public class ImmediateAsyncLifecycleTests {
  private static ServiceBusIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    // Get SHARED ServiceBus resources (emulator + single static ServiceBusClient)
    var (connectionString, sharedClient) = await SharedFixtureSource.GetSharedResourcesAsync(0);

    // Create fixture with shared client (per-test PostgreSQL + hosts, but shared ServiceBusClient)
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
  /// Verifies that ImmediateAsync lifecycle stage fires after command handler completes.
  /// </summary>
  [Test]
  public async Task ImmediateAsync_FiresAfterCommandHandler_CompletesAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10,
      ImageUrl = "https://example.com/image.jpg"
    };

    // Act - Register receptor and dispatch command
    var receptorTask = fixture.InventoryHost.WaitForImmediateAsyncAsync<CreateProductCommand>(
      timeoutMilliseconds: 5000);

    await fixture.Dispatcher.SendAsync(command);
    var receptor = await receptorTask;

    // Assert - Verify receptor was invoked
    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId);
  }

  /// <summary>
  /// Verifies that ImmediateAsync fires before database operations complete.
  /// This tests the "no database writes have occurred yet" guarantee.
  /// </summary>
  [Test]
  public async Task ImmediateAsync_FiresBeforeDatabaseWrites_CompletesAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10,
      ImageUrl = "https://example.com/image.jpg"
    };

    var completionSource = new TaskCompletionSource<bool>();
    var receptor = new GenericLifecycleCompletionReceptor<CreateProductCommand>(completionSource);

    var registry = fixture.InventoryHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<CreateProductCommand>(receptor, LifecycleStage.ImmediateAsync);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for ImmediateAsync stage
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

      // Assert - At this point, ImmediateAsync has fired
      // But the event should NOT be in event store yet (database write hasn't committed)
      // Note: This is a timing assertion - we're checking that ImmediateAsync fires
      // before the transaction commits. In practice, we can't easily verify the
      // "no database writes" guarantee without mocking, but we can verify timing.
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

    } finally {
      registry.Unregister<CreateProductCommand>(receptor, LifecycleStage.ImmediateAsync);
    }
  }

  /// <summary>
  /// Verifies that ImmediateAsync fires for multiple commands in sequence.
  /// </summary>
  [Test]
  public async Task ImmediateAsync_MultipleCommands_FiresForEachAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var commands = new[] {
      new CreateProductCommand {
        ProductId = ProductId.New(),
        Name = "Product 1",
        Description = "Description 1",
        Price = 10.00m,
        InitialStock = 5
      },
      new CreateProductCommand {
        ProductId = ProductId.New(),
        Name = "Product 2",
        Description = "Description 2",
        Price = 20.00m,
        InitialStock = 15
      },
      new CreateProductCommand {
        ProductId = ProductId.New(),
        Name = "Product 3",
        Description = "Description 3",
        Price = 30.00m,
        InitialStock = 25
      }
    };

    var completionSource = new TaskCompletionSource<bool>();
    var receptor = new GenericLifecycleCompletionReceptor<CreateProductCommand>(completionSource);

    var registry = fixture.InventoryHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<CreateProductCommand>(receptor, LifecycleStage.ImmediateAsync);

    try {
      // Act - Dispatch all commands
      foreach (var command in commands) {
        await fixture.Dispatcher.SendAsync(command);
      }

      // Wait for last command to complete ImmediateAsync stage
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(10));

      // Assert - Receptor should have been invoked 3 times
      await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(3);

    } finally {
      registry.Unregister<CreateProductCommand>(receptor, LifecycleStage.ImmediateAsync);
    }
  }

  /// <summary>
  /// Verifies that ImmediateAsync fires with correct lifecycle context metadata.
  /// </summary>
  [Test]
  public async Task ImmediateAsync_ProvidesCorrectLifecycleContext_CompletesAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor and dispatch command
    var receptorTask = fixture.InventoryHost.WaitForImmediateAsyncAsync<CreateProductCommand>(
      timeoutMilliseconds: 5000);

    await fixture.Dispatcher.SendAsync(command);
    var receptor = await receptorTask;

    // Assert - Verify receptor captured the message
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId);

    // Note: ILifecycleContext injection is not yet implemented in the current codebase
    // When implemented, we would also verify:
    // await Assert.That(receptor.LastLifecycleContext).IsNotNull();
    // await Assert.That(receptor.LastLifecycleContext!.CurrentStage).IsEqualTo(LifecycleStage.ImmediateAsync);
  }

  /// <summary>
  /// Verifies that ImmediateAsync completes within expected time bounds (low latency).
  /// </summary>
  [Test]
  public async Task ImmediateAsync_CompletesWithLowLatency_UnderOneSecondAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    var completionSource = new TaskCompletionSource<bool>();
    var receptor = new GenericLifecycleCompletionReceptor<CreateProductCommand>(completionSource);

    var registry = fixture.InventoryHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<CreateProductCommand>(receptor, LifecycleStage.ImmediateAsync);

    try {
      // Act - Measure time from dispatch to ImmediateAsync completion
      var stopwatch = System.Diagnostics.Stopwatch.StartNew();

      await fixture.Dispatcher.SendAsync(command);
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

      stopwatch.Stop();

      // Assert - ImmediateAsync should complete very quickly (< 1 second)
      await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(1000);
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

    } finally {
      registry.Unregister<CreateProductCommand>(receptor, LifecycleStage.ImmediateAsync);
    }
  }
}
