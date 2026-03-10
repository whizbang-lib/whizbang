using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for Dispatcher behavior when the service provider is disposed.
/// </summary>
/// <remarks>
/// <para>
/// These tests verify that the dispatcher handles disposal scenarios gracefully,
/// particularly when PublishToOutboxAsync is called after the service provider
/// has been disposed (e.g., during application shutdown or debugging hot reload).
/// </para>
/// </remarks>
/// <tests>src/Whizbang.Core/Dispatcher.cs:PublishToOutboxAsync</tests>
public class DispatcherDisposalTests {
  // Test event for publishing
  public record TestEvent(string Data);

  [Test]
  public async Task PublishAsync_WhenServiceProviderDisposed_ShouldNotThrowObjectDisposedExceptionAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Register service instance provider (required dependency)
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    // Register receptors and dispatcher
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    // Dispose the service provider to simulate shutdown scenario
    await serviceProvider.DisposeAsync();

    // Act - Try to publish and capture any exception with full stack trace
    Exception? caughtException = null;
    try {
      await dispatcher.PublishAsync(new TestEvent("test"));
    } catch (Exception ex) {
      caughtException = ex;
      Console.WriteLine($"=== EXCEPTION TYPE: {ex.GetType().FullName}");
      Console.WriteLine($"=== MESSAGE: {ex.Message}");
      Console.WriteLine($"=== STACK TRACE:\n{ex.StackTrace}");
    }

    // Assert - The dispatcher should NOT throw ObjectDisposedException
    // Instead it should handle disposal gracefully
    await Assert.That(caughtException).IsNull()
      .Because($"Dispatcher should handle disposal gracefully, but threw: {caughtException?.GetType().Name}: {caughtException?.Message}");
  }

}
