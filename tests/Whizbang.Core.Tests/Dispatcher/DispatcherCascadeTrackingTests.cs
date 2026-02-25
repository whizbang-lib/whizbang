using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Generated;
using Whizbang.Core.Internal;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Common;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for event tracking in cascade operations for perspective synchronization.
/// When events are cascaded from receptor results (via Route.Local, Route.Both, etc.),
/// they must be tracked by IScopedEventTracker for perspective sync to work correctly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Bug Background:</strong>
/// Events cascaded via <c>Route.Local()</c> bypass the event store and were never tracked
/// by <see cref="IScopedEventTracker"/>. This caused the sync awaiter to return immediately
/// (no events to wait for), resulting in race conditions where perspectives hadn't processed
/// events yet.
/// </para>
/// <para>
/// <strong>Fix:</strong>
/// Track events in <c>_cascadeEventsFromResultAsync</c> before dispatching them locally.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
public class DispatcherCascadeTrackingTests : DiagnosticTestBase {
  protected override DiagnosticCategory DiagnosticCategories => DiagnosticCategory.ReceptorDiscovery;

  #region Test Messages

  /// <summary>
  /// Command that triggers event cascade.
  /// </summary>
  public record CascadeTrackingCommand(Guid OrderId);

  /// <summary>
  /// Event to be cascaded and tracked.
  /// </summary>
  public record CascadeTrackingEvent([property: StreamId] Guid OrderId) : IEvent;

  /// <summary>
  /// Non-event message (should NOT be tracked).
  /// </summary>
  public record CascadeTrackingResponse(bool Success);

  #endregion

  #region Test Infrastructure

  /// <summary>
  /// Simple service scope factory for testing.
  /// </summary>
  private sealed class TestServiceScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new TestServiceScope(provider);
  }

  /// <summary>
  /// Simple service scope for testing.
  /// </summary>
  private sealed class TestServiceScope(IServiceProvider provider) : IServiceScope {
    public IServiceProvider ServiceProvider { get; } = provider;
    public void Dispose() { }
  }

  /// <summary>
  /// Test dispatcher that cascades events with tracking support.
  /// </summary>
  private sealed class CascadeTrackingTestDispatcher : Core.Dispatcher {
    private readonly Func<object, (object message, DispatchMode mode)>? _cascadeResult;
    private readonly List<object> _localInvocations = [];
    private readonly object _lock = new();

    public CascadeTrackingTestDispatcher(
      IServiceProvider serviceProvider,
      IScopedEventTracker? tracker = null,
      IStreamIdExtractor? streamIdExtractor = null,
      Func<object, (object message, DispatchMode mode)>? cascadeResult = null)
        : base(
          serviceProvider,
          new ServiceInstanceProvider(configuration: null),
          scopedEventTracker: tracker,
          streamIdExtractor: streamIdExtractor) {
      _cascadeResult = cascadeResult;
    }

    public List<object> GetLocalInvocations() {
      lock (_lock) {
        return _localInvocations.ToList();
      }
    }

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      // Handle CascadeTrackingCommand -> Routed<CascadeTrackingEvent>
      if (messageType == typeof(CascadeTrackingCommand) && typeof(TResult) == typeof(Routed<CascadeTrackingEvent>)) {
        return msg => {
          var cmd = (CascadeTrackingCommand)msg;
          var evt = new CascadeTrackingEvent(cmd.OrderId);
          var routed = Route.Local(evt);
          return ValueTask.FromResult((TResult)(object)routed);
        };
      }
      // Handle variant with Route.Outbox
      if (messageType == typeof(CascadeTrackingCommand) && typeof(TResult) == typeof((CascadeTrackingResponse, Routed<CascadeTrackingEvent>))) {
        return msg => {
          var cmd = (CascadeTrackingCommand)msg;
          var response = new CascadeTrackingResponse(true);
          var evt = new CascadeTrackingEvent(cmd.OrderId);
          var routed = Route.Outbox(evt);
          return ValueTask.FromResult((TResult)(object)(response, routed));
        };
      }
      // Handle variant with Route.Both
      if (messageType == typeof(CascadeTrackingCommand) && typeof(TResult) == typeof((CascadeTrackingResponse, Routed<CascadeTrackingEvent>, bool))) {
        return msg => {
          var cmd = (CascadeTrackingCommand)msg;
          var response = new CascadeTrackingResponse(true);
          var evt = new CascadeTrackingEvent(cmd.OrderId);
          var routed = Route.Both(evt);
          return ValueTask.FromResult((TResult)(object)(response, routed, true));
        };
      }
      return null;
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      return null;
    }

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) {
      return evt => {
        lock (_lock) {
          _localInvocations.Add(evt!);
        }
        return Task.CompletedTask;
      };
    }

    protected override Func<object, Task>? GetUntypedReceptorPublisher(Type eventType) {
      return evt => {
        lock (_lock) {
          _localInvocations.Add(evt);
        }
        return Task.CompletedTask;
      };
    }

    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) {
      return null;
    }

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) {
      return null;
    }

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) {
      return null;
    }

    protected override DispatchMode? GetReceptorDefaultRouting(Type messageType) {
      return null;
    }
  }

  /// <summary>
  /// Simple stream ID extractor for testing.
  /// </summary>
  private sealed class TestStreamIdExtractor : IStreamIdExtractor {
    public Guid? ExtractStreamId(object message, Type messageType) {
      return message switch {
        CascadeTrackingEvent evt => evt.OrderId,
        _ => null
      };
    }
  }

  #endregion

  #region Route.Local Tracking Tests

  /// <summary>
  /// Verifies that events cascaded via Route.Local are tracked by IScopedEventTracker.
  /// This is the critical test - Route.Local events were previously NOT tracked.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task CascadeFromResult_WithRouteLocal_TracksEventAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamIdExtractor = new TestStreamIdExtractor();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(new TestServiceScopeFactory(services.BuildServiceProvider()));
    var provider = services.BuildServiceProvider();
    var dispatcher = new CascadeTrackingTestDispatcher(provider, tracker, streamIdExtractor);
    var orderId = Guid.NewGuid();
    var command = new CascadeTrackingCommand(orderId);

    // Act - Invoke and let cascade happen
    _ = await dispatcher.LocalInvokeAsync<Routed<CascadeTrackingEvent>>(command);

    // Assert - Event should be tracked
    var trackedEvents = tracker.GetEmittedEvents();
    await Assert.That(trackedEvents.Count).IsEqualTo(1)
      .Because("Route.Local events MUST be tracked for perspective sync");
    await Assert.That(trackedEvents[0].EventType).IsEqualTo(typeof(CascadeTrackingEvent));
    await Assert.That(trackedEvents[0].StreamId).IsEqualTo(orderId)
      .Because("StreamId should be extracted from the event");
  }

  /// <summary>
  /// Verifies that events cascaded via Route.Outbox are tracked by IScopedEventTracker.
  /// These are also tracked by SyncTrackingEventStoreDecorator, but we track here too
  /// to ensure consistency and handle edge cases.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task CascadeFromResult_WithRouteOutbox_TracksEventAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamIdExtractor = new TestStreamIdExtractor();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(new TestServiceScopeFactory(services.BuildServiceProvider()));
    var provider = services.BuildServiceProvider();
    var dispatcher = new CascadeTrackingTestDispatcher(provider, tracker, streamIdExtractor);
    var orderId = Guid.NewGuid();
    var command = new CascadeTrackingCommand(orderId);

    // Act
    _ = await dispatcher.LocalInvokeAsync<(CascadeTrackingResponse, Routed<CascadeTrackingEvent>)>(command);

    // Assert
    var trackedEvents = tracker.GetEmittedEvents();
    await Assert.That(trackedEvents.Count).IsEqualTo(1)
      .Because("Route.Outbox events should be tracked for consistency");
    await Assert.That(trackedEvents[0].EventType).IsEqualTo(typeof(CascadeTrackingEvent));
  }

  /// <summary>
  /// Verifies that events cascaded via Route.Both are tracked by IScopedEventTracker.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task CascadeFromResult_WithRouteBoth_TracksEventAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamIdExtractor = new TestStreamIdExtractor();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(new TestServiceScopeFactory(services.BuildServiceProvider()));
    var provider = services.BuildServiceProvider();
    var dispatcher = new CascadeTrackingTestDispatcher(provider, tracker, streamIdExtractor);
    var orderId = Guid.NewGuid();
    var command = new CascadeTrackingCommand(orderId);

    // Act
    _ = await dispatcher.LocalInvokeAsync<(CascadeTrackingResponse, Routed<CascadeTrackingEvent>, bool)>(command);

    // Assert
    var trackedEvents = tracker.GetEmittedEvents();
    await Assert.That(trackedEvents.Count).IsEqualTo(1)
      .Because("Route.Both events should be tracked");
    await Assert.That(trackedEvents[0].EventType).IsEqualTo(typeof(CascadeTrackingEvent));
  }

  #endregion

  #region Edge Cases

  /// <summary>
  /// Verifies that cascade works without throwing when no tracker is injected.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task CascadeFromResult_NoTracker_DoesNotThrowAsync() {
    // Arrange - NO tracker injected
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(new TestServiceScopeFactory(services.BuildServiceProvider()));
    var provider = services.BuildServiceProvider();
    var dispatcher = new CascadeTrackingTestDispatcher(provider, tracker: null);
    var command = new CascadeTrackingCommand(Guid.NewGuid());

    // Act & Assert - Should not throw
    var routed = await dispatcher.LocalInvokeAsync<Routed<CascadeTrackingEvent>>(command);
    await Assert.That(routed.Value).IsNotNull();
  }

  /// <summary>
  /// Verifies that cascade with no stream ID extractor uses Guid.Empty.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task CascadeFromResult_NoStreamIdExtractor_UsesEmptyGuidAsync() {
    // Arrange - Tracker but NO stream ID extractor
    var tracker = new ScopedEventTracker();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(new TestServiceScopeFactory(services.BuildServiceProvider()));
    var provider = services.BuildServiceProvider();
    var dispatcher = new CascadeTrackingTestDispatcher(provider, tracker, streamIdExtractor: null);
    var command = new CascadeTrackingCommand(Guid.NewGuid());

    // Act
    _ = await dispatcher.LocalInvokeAsync<Routed<CascadeTrackingEvent>>(command);

    // Assert - Should track with Guid.Empty as streamId
    var trackedEvents = tracker.GetEmittedEvents();
    await Assert.That(trackedEvents.Count).IsEqualTo(1);
    await Assert.That(trackedEvents[0].StreamId).IsEqualTo(Guid.Empty)
      .Because("Without extractor, StreamId should default to Guid.Empty");
  }

  #endregion
}
