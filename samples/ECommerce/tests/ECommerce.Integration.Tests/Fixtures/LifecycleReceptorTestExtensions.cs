using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Messaging;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Extension methods for using lifecycle receptors in integration tests.
/// Provides deterministic synchronization by waiting for perspective processing instead of polling.
/// </summary>
/// <docs>testing/lifecycle-synchronization</docs>
public static class LifecycleReceptorTestExtensions {

  /// <summary>
  /// Waits for perspective processing to complete for a specific event type.
  /// Uses PostPerspectiveInline lifecycle stage for deterministic synchronization.
  /// </summary>
  /// <typeparam name="TEvent">The event type to wait for.</typeparam>
  /// <param name="host">The host containing the ILifecycleReceptorRegistry service.</param>
  /// <param name="perspectiveName">Optional perspective name to filter by (null = any perspective).</param>
  /// <param name="timeoutMilliseconds">Maximum time to wait in milliseconds (default: 15000ms).</param>
  /// <exception cref="TimeoutException">Thrown if perspective processing doesn't complete within timeout.</exception>
  /// <remarks>
  /// <para>
  /// This method eliminates race conditions by waiting for the actual perspective processing to complete
  /// instead of polling database tables. The PostPerspectiveInline lifecycle stage guarantees that
  /// perspective data is persisted before the receptor completes.
  /// </para>
  /// <para>
  /// <strong>Usage:</strong>
  /// </para>
  /// <code>
  /// // Dispatch command that triggers perspective update
  /// await dispatcher.SendAsync(createProductCommand);
  ///
  /// // Wait for perspective processing to complete (deterministic, no race condition!)
  /// await host.WaitForPerspectiveCompletionAsync&lt;ProductCreatedEvent&gt;(
  ///   perspectiveName: "ProductCatalog",
  ///   timeoutMilliseconds: 15000
  /// );
  ///
  /// // Now verify perspective data - guaranteed to be saved
  /// var product = await productLens.GetByIdAsync(createProductCommand.ProductId);
  /// Assert.That(product).IsNotNull();
  /// </code>
  /// </remarks>
  public static async Task WaitForPerspectiveCompletionAsync<TEvent>(
    this IHost host,
    string? perspectiveName = null,
    int timeoutMilliseconds = 15000)
    where TEvent : IEvent {

    ArgumentNullException.ThrowIfNull(host);

    // Create completion source for signaling
    var completionSource = new TaskCompletionSource<bool>();

    // Create receptor that will signal completion
    var receptor = new PerspectiveCompletionReceptor<TEvent>(completionSource, perspectiveName);

    // Get registry from host
    var registry = host.Services.GetRequiredService<ILifecycleReceptorRegistry>();

    // Register receptor at PostPerspectiveInline stage (blocking, guarantees persistence)
    registry.Register<TEvent>(receptor, LifecycleStage.PostPerspectiveInline);

    try {
      // Wait for completion with timeout
      await completionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds));
    } finally {
      // Always unregister, even if timeout occurs
      registry.Unregister<TEvent>(receptor, LifecycleStage.PostPerspectiveInline);
    }
  }

  /// <summary>
  /// Waits for multiple perspective processing completions for different event types.
  /// Useful when a command triggers multiple events that update different perspectives.
  /// </summary>
  /// <param name="host">The host containing the ILifecycleReceptorRegistry service.</param>
  /// <param name="eventTypes">Array of event types to wait for.</param>
  /// <param name="perspectiveName">Optional perspective name to filter by (null = any perspective).</param>
  /// <param name="timeoutMilliseconds">Maximum time to wait in milliseconds (default: 15000ms).</param>
  /// <exception cref="TimeoutException">Thrown if any perspective processing doesn't complete within timeout.</exception>
  /// <remarks>
  /// <para>
  /// This method waits for ALL specified event types to be processed.
  /// If you need to wait for only ONE of several events, use multiple calls to <see cref="WaitForPerspectiveCompletionAsync{TEvent}"/> with Task.WhenAny.
  /// </para>
  /// <para>
  /// <strong>Usage:</strong>
  /// </para>
  /// <code>
  /// // Dispatch command that triggers multiple events
  /// await dispatcher.SendAsync(updateInventoryCommand);
  ///
  /// // Wait for all perspective updates to complete
  /// await host.WaitForMultiplePerspectiveCompletionsAsync(
  ///   new[] { typeof(InventoryUpdatedEvent), typeof(ProductModifiedEvent) },
  ///   timeoutMilliseconds: 20000
  /// );
  ///
  /// // Now verify all perspective data
  /// var inventory = await inventoryLens.GetByIdAsync(productId);
  /// var product = await productLens.GetByIdAsync(productId);
  /// </code>
  /// </remarks>
  public static async Task WaitForMultiplePerspectiveCompletionsAsync(
    this IHost host,
    Type[] eventTypes,
    string? perspectiveName = null,
    int timeoutMilliseconds = 15000) {

    ArgumentNullException.ThrowIfNull(host);
    ArgumentNullException.ThrowIfNull(eventTypes);

    if (eventTypes.Length == 0) {
      return;  // Nothing to wait for
    }

    // Create completion sources for each event type
    var completionSources = eventTypes.Select(_ => new TaskCompletionSource<bool>()).ToArray();
    var receptors = new List<object>();
    var registry = host.Services.GetRequiredService<ILifecycleReceptorRegistry>();

    try {
      // Register receptors for each event type
      for (var i = 0; i < eventTypes.Length; i++) {
        var eventType = eventTypes[i];
        var completionSource = completionSources[i];

        // Create receptor using reflection (since we don't know TEvent at compile time)
        var receptorType = typeof(PerspectiveCompletionReceptor<>).MakeGenericType(eventType);
        var receptor = Activator.CreateInstance(receptorType, completionSource, perspectiveName, null)
          ?? throw new InvalidOperationException($"Failed to create receptor for event type {eventType.Name}");

        receptors.Add(receptor);

        // Register using reflection (since we don't know TEvent at compile time)
        var registerMethod = typeof(ILifecycleReceptorRegistry).GetMethod(nameof(ILifecycleReceptorRegistry.Register))!
          .MakeGenericMethod(eventType);
        registerMethod.Invoke(registry, new[] { receptor, LifecycleStage.PostPerspectiveInline });
      }

      // Wait for all completions with timeout
      await Task.WhenAll(completionSources.Select(cs => cs.Task))
        .WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds));

    } finally {
      // Unregister all receptors
      for (var i = 0; i < eventTypes.Length; i++) {
        var eventType = eventTypes[i];
        var receptor = receptors[i];

        var unregisterMethod = typeof(ILifecycleReceptorRegistry).GetMethod(nameof(ILifecycleReceptorRegistry.Unregister))!
          .MakeGenericMethod(eventType);
        unregisterMethod.Invoke(registry, new[] { receptor, LifecycleStage.PostPerspectiveInline });
      }
    }
  }
}
