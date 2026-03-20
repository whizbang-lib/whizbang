using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Tests.Generated;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for void LocalInvokeAsync cascade behavior.
/// When calling void LocalInvokeAsync (no result expected), the dispatcher should still
/// cascade any events returned by non-void receptors.
/// </summary>
/// <remarks>
/// The bug: void LocalInvokeAsync paths only look for void receptors.
/// If a receptor implements IReceptor&lt;TMessage, TResponse&gt; (non-void),
/// calling void LocalInvokeAsync won't find it OR cascade its return value.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
public class DispatcherVoidCascadeTests {
  #region Test Messages

  /// <summary>
  /// Command that will be handled by a non-void receptor (returns events).
  /// </summary>
  public record ProcessOrderCommand(Guid OrderId, Guid CustomerId);

  /// <summary>
  /// Event returned by the receptor that should be cascaded.
  /// Uses [DefaultRouting(Local)] to ensure local cascade for test verification.
  /// (Default system routing is Outbox for cross-service delivery)
  /// </summary>
  [DefaultRouting(DispatchMode.Local)]
  public record OrderProcessedEvent([property: StreamId] Guid OrderId, Guid CustomerId) : IEvent;

  /// <summary>
  /// Result DTO returned alongside the event.
  /// </summary>
  public record ProcessOrderResult(Guid OrderId, bool Success);

  #endregion

  #region Event Tracking

  /// <summary>
  /// Tracks events that have been published through the cascade mechanism.
  /// </summary>
  public static class VoidCascadeEventTracker {
    private static readonly List<IEvent> _publishedEvents = [];
    private static readonly Lock _lock = new();

    public static void Reset() {
      lock (_lock) {
        _publishedEvents.Clear();
      }
    }

    public static void Track(IEvent evt) {
      lock (_lock) {
        _publishedEvents.Add(evt);
      }
    }

    public static IReadOnlyList<IEvent> GetPublishedEvents() {
      lock (_lock) {
        return [.. _publishedEvents];
      }
    }

    public static int Count {
      get {
        lock (_lock) {
          return _publishedEvents.Count;
        }
      }
    }
  }

  #endregion

  #region Test Receptors

  /// <summary>
  /// Non-void receptor that returns a tuple with result and event.
  /// When called via void LocalInvokeAsync, the event should still be cascaded.
  /// </summary>
  public class ProcessOrderReceptor : IReceptor<ProcessOrderCommand, (ProcessOrderResult, OrderProcessedEvent)> {
    public ValueTask<(ProcessOrderResult, OrderProcessedEvent)> HandleAsync(
        ProcessOrderCommand message,
        CancellationToken cancellationToken = default) {
      var result = new ProcessOrderResult(message.OrderId, true);
      var evt = new OrderProcessedEvent(message.OrderId, message.CustomerId);
      return ValueTask.FromResult((result, evt));
    }
  }

  /// <summary>
  /// Event tracking receptor that records OrderProcessedEvent publications.
  /// </summary>
  public class OrderProcessedEventTracker : IReceptor<OrderProcessedEvent> {
    public ValueTask HandleAsync(OrderProcessedEvent message, CancellationToken cancellationToken = default) {
      VoidCascadeEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  #endregion

  #region Tests

  /// <summary>
  /// When calling void LocalInvokeAsync on a command handled by a non-void receptor,
  /// the dispatcher should find the receptor, invoke it, and cascade any returned events.
  /// </summary>
  /// <remarks>
  /// BUG: Currently void LocalInvokeAsync only looks for void receptors (IReceptor&lt;TMessage&gt;).
  /// It should fall back to non-void receptors (IReceptor&lt;TMessage, TResponse&gt;) and cascade their results.
  /// </remarks>
  [Test]
  [NotInParallel]
  public async Task VoidLocalInvokeAsync_WithNonVoidReceptor_CascadesReturnedEventsAsync() {
    // Arrange
    VoidCascadeEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var command = new ProcessOrderCommand(Guid.NewGuid(), Guid.NewGuid());

    // Act - Use void LocalInvokeAsync (no expected result type)
    // This should still find the non-void receptor and cascade its events
    await dispatcher.LocalInvokeAsync(command);

    // Assert - The event should be cascaded and tracked
    await Assert.That(VoidCascadeEventTracker.Count).IsEqualTo(1)
      .Because("Void LocalInvokeAsync should cascade events from non-void receptor returns");

    var publishedEvent = VoidCascadeEventTracker.GetPublishedEvents()[0] as OrderProcessedEvent;
    await Assert.That(publishedEvent).IsNotNull();
    await Assert.That(publishedEvent!.OrderId).IsEqualTo(command.OrderId);
  }

  /// <summary>
  /// When calling generic void LocalInvokeAsync&lt;TMessage&gt; on a command handled by a non-void receptor,
  /// the dispatcher should find the receptor, invoke it, and cascade any returned events.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task GenericVoidLocalInvokeAsync_WithNonVoidReceptor_CascadesReturnedEventsAsync() {
    // Arrange
    VoidCascadeEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var command = new ProcessOrderCommand(Guid.NewGuid(), Guid.NewGuid());

    // Act - Use generic void LocalInvokeAsync<TMessage> (type-safe, no result)
    await dispatcher.LocalInvokeAsync<ProcessOrderCommand>(command);

    // Assert - The event should be cascaded and tracked
    await Assert.That(VoidCascadeEventTracker.Count).IsEqualTo(1)
      .Because("Generic void LocalInvokeAsync<TMessage> should cascade events from non-void receptor returns");
  }

  /// <summary>
  /// When using void LocalInvokeAsync with a context, events should still cascade.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task VoidLocalInvokeAsync_WithContext_CascadesReturnedEventsAsync() {
    // Arrange
    VoidCascadeEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var command = new ProcessOrderCommand(Guid.NewGuid(), Guid.NewGuid());
    var context = MessageContext.New();

    // Act - Use void LocalInvokeAsync with explicit context
    await dispatcher.LocalInvokeAsync(command, context);

    // Assert - The event should be cascaded and tracked
    await Assert.That(VoidCascadeEventTracker.Count).IsEqualTo(1)
      .Because("Void LocalInvokeAsync with context should cascade events from non-void receptor returns");
  }

  /// <summary>
  /// When using void LocalInvokeAsync with DispatchOptions, events should still cascade.
  /// This tests the _localInvokeVoidWithOptionsAsync path.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task VoidLocalInvokeAsync_WithDispatchOptions_CascadesReturnedEventsAsync() {
    // Arrange
    VoidCascadeEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var command = new ProcessOrderCommand(Guid.NewGuid(), Guid.NewGuid());
    var options = new DispatchOptions { CancellationToken = CancellationToken.None };

    // Act - Use void LocalInvokeAsync with DispatchOptions
    await dispatcher.LocalInvokeAsync(command, options);

    // Assert - The event should be cascaded and tracked
    await Assert.That(VoidCascadeEventTracker.Count).IsEqualTo(1)
      .Because("Void LocalInvokeAsync with DispatchOptions should cascade events from non-void receptor returns");
  }

  #endregion

  #region Helper Methods

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();

    // Register service instance provider (required dependency)
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

    // Register all receptors including our test receptors
    services.AddReceptors();

    // Register dispatcher
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  #endregion
}
