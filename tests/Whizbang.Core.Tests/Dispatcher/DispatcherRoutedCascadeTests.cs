using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Generated;
using Whizbang.Core.Internal;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Common;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for routed cascade behavior in LocalInvokeAsync.
/// When a receptor returns messages, the dispatcher cascades them based on their DispatchMode:
/// - Local: Invoke local receptors only
/// - Outbox: Write to outbox only (for cross-service delivery)
/// - Both: Invoke local AND write to outbox
/// - Default (unwrapped): Outbox only (system default)
/// </summary>
/// <remarks>
/// Per the routed cascade design:
/// - Unwrapped messages default to DispatchMode.Outbox
/// - Route.Local() wraps a message with DispatchMode.Local
/// - Route.Outbox() wraps a message with DispatchMode.Outbox
/// - Route.Both() wraps a message with DispatchMode.Both
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
public class DispatcherRoutedCascadeTests : DiagnosticTestBase {
  protected override DiagnosticCategory DiagnosticCategories => DiagnosticCategory.ReceptorDiscovery;

  #region Test Messages

  /// <summary>
  /// Command that will be handled by a test receptor.
  /// </summary>
  public record RoutedTestCommand(Guid OrderId);

  /// <summary>
  /// Event to be cascaded with routing.
  /// </summary>
  public record RoutedTestEvent([property: StreamId] Guid OrderId) : IEvent;

  /// <summary>
  /// Result returned by receptors.
  /// </summary>
  public record RoutedTestResult(bool Success);

  #endregion

  #region Tracking Infrastructure

  /// <summary>
  /// Tracks local receptor invocations.
  /// </summary>
  public static class RoutedCascadeTracker {
    private static readonly List<object> _localInvocations = [];
    private static readonly List<object> _outboxPublications = [];
    private static readonly object _lock = new();

    public static void Reset() {
      lock (_lock) {
        _localInvocations.Clear();
        _outboxPublications.Clear();
      }
    }

    public static void TrackLocal(object evt) {
      lock (_lock) {
        _localInvocations.Add(evt);
      }
    }

    public static void TrackOutbox(object msg) {
      lock (_lock) {
        _outboxPublications.Add(msg);
      }
    }

    public static int LocalCount {
      get {
        lock (_lock) {
          return _localInvocations.Count;
        }
      }
    }

    public static int OutboxCount {
      get {
        lock (_lock) {
          return _outboxPublications.Count;
        }
      }
    }

    public static IReadOnlyList<object> GetLocalInvocations() {
      lock (_lock) {
        return _localInvocations.ToList();
      }
    }

    public static IReadOnlyList<object> GetOutboxPublications() {
      lock (_lock) {
        return _outboxPublications.ToList();
      }
    }
  }

  #endregion

  #region MessageExtractor Tests - Unit Tests for Routing Extraction

  /// <summary>
  /// Verifies that ExtractMessagesWithRouting correctly extracts unwrapped messages with default Outbox routing.
  /// Default is Outbox for cross-service delivery per routed cascade design.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_UnwrappedMessage_DefaultsToOutboxAsync() {
    // Arrange
    var evt = new RoutedTestEvent(Guid.NewGuid());

    // Act
    var extracted = MessageExtractor.ExtractMessagesWithRouting(evt).ToList();

    // Assert
    await Assert.That(extracted).Count().IsEqualTo(1);
    await Assert.That(extracted[0].Mode).IsEqualTo(DispatchMode.Outbox)
      .Because("Unwrapped messages should default to Outbox routing for cross-service delivery");
  }

  /// <summary>
  /// Verifies that Route.Local() sets Local mode.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_RouteLocal_SetsLocalModeAsync() {
    // Arrange
    var evt = new RoutedTestEvent(Guid.NewGuid());
    var routed = Route.Local(evt);

    // Act
    var extracted = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(extracted).Count().IsEqualTo(1);
    await Assert.That(extracted[0].Mode).IsEqualTo(DispatchMode.Local);
  }

  /// <summary>
  /// Verifies that Route.Outbox() sets Outbox mode.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_RouteOutbox_SetsOutboxModeAsync() {
    // Arrange
    var evt = new RoutedTestEvent(Guid.NewGuid());
    var routed = Route.Outbox(evt);

    // Act
    var extracted = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(extracted).Count().IsEqualTo(1);
    await Assert.That(extracted[0].Mode).IsEqualTo(DispatchMode.Outbox);
  }

  /// <summary>
  /// Verifies that Route.Both() sets Both mode.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_RouteBoth_SetsBothModeAsync() {
    // Arrange
    var evt = new RoutedTestEvent(Guid.NewGuid());
    var routed = Route.Both(evt);

    // Act
    var extracted = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(extracted).Count().IsEqualTo(1);
    await Assert.That(extracted[0].Mode).IsEqualTo(DispatchMode.Both);
  }

  /// <summary>
  /// Verifies that receptor default routing is applied when no wrapper is used.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_WithReceptorDefault_UsesReceptorDefaultAsync() {
    // Arrange
    var evt = new RoutedTestEvent(Guid.NewGuid());

    // Act - Pass receptor default of Local
    var extracted = MessageExtractor.ExtractMessagesWithRouting(evt, receptorDefault: DispatchMode.Local).ToList();

    // Assert
    await Assert.That(extracted).Count().IsEqualTo(1);
    await Assert.That(extracted[0].Mode).IsEqualTo(DispatchMode.Local)
      .Because("Receptor default should override system default when no wrapper is used");
  }

  /// <summary>
  /// Verifies that tuple extraction preserves routing for each item.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_TupleWithMixedRouting_PreservesPerItemRoutingAsync() {
    // Arrange
    var evt1 = new RoutedTestEvent(Guid.NewGuid());
    var evt2 = new RoutedTestEvent(Guid.NewGuid());
    var tuple = (Route.Local(evt1), Route.Outbox(evt2));

    // Act
    var extracted = MessageExtractor.ExtractMessagesWithRouting(tuple).ToList();

    // Assert
    await Assert.That(extracted).Count().IsEqualTo(2);
    await Assert.That(extracted[0].Mode).IsEqualTo(DispatchMode.Local)
      .Because("First item has Local routing");
    await Assert.That(extracted[1].Mode).IsEqualTo(DispatchMode.Outbox)
      .Because("Second item has Outbox routing");
  }

  /// <summary>
  /// Verifies that array wrapper applies routing to all items.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_ArrayWithWrapper_AppliesRoutingToAllItemsAsync() {
    // Arrange
    var events = new IEvent[] {
      new RoutedTestEvent(Guid.NewGuid()),
      new RoutedTestEvent(Guid.NewGuid())
    };
    var routed = Route.Local(events);

    // Act
    var extracted = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(extracted).Count().IsEqualTo(2);
    await Assert.That(extracted[0].Mode).IsEqualTo(DispatchMode.Local);
    await Assert.That(extracted[1].Mode).IsEqualTo(DispatchMode.Local);
  }

  /// <summary>
  /// Verifies that Route.LocalNoPersist() sets LocalNoPersist mode.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_RouteLocalNoPersist_SetsLocalNoPersistModeAsync() {
    // Arrange
    var evt = new RoutedTestEvent(Guid.NewGuid());
    var routed = Route.LocalNoPersist(evt);

    // Act
    var extracted = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(extracted).Count().IsEqualTo(1);
    await Assert.That(extracted[0].Mode).IsEqualTo(DispatchMode.LocalNoPersist);
  }

  /// <summary>
  /// Verifies that Route.EventStoreOnly() sets EventStoreOnly mode.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_RouteEventStoreOnly_SetsEventStoreOnlyModeAsync() {
    // Arrange
    var evt = new RoutedTestEvent(Guid.NewGuid());
    var routed = Route.EventStoreOnly(evt);

    // Act
    var extracted = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(extracted).Count().IsEqualTo(1);
    await Assert.That(extracted[0].Mode).IsEqualTo(DispatchMode.EventStoreOnly);
  }

  /// <summary>
  /// Verifies that LocalNoPersist has LocalDispatch flag but NOT EventStore flag.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_LocalNoPersist_HasLocalDispatchButNotEventStoreAsync() {
    // Arrange
    var evt = new RoutedTestEvent(Guid.NewGuid());
    var routed = Route.LocalNoPersist(evt);

    // Act
    var extracted = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(extracted[0].Mode.HasFlag(DispatchMode.LocalDispatch)).IsTrue()
      .Because("LocalNoPersist should invoke local receptors");
    await Assert.That(extracted[0].Mode.HasFlag(DispatchMode.EventStore)).IsFalse()
      .Because("LocalNoPersist should NOT persist to event store");
  }

  /// <summary>
  /// Verifies that EventStoreOnly has EventStore flag but NOT LocalDispatch flag.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_EventStoreOnly_HasEventStoreButNotLocalDispatchAsync() {
    // Arrange
    var evt = new RoutedTestEvent(Guid.NewGuid());
    var routed = Route.EventStoreOnly(evt);

    // Act
    var extracted = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(extracted[0].Mode.HasFlag(DispatchMode.EventStore)).IsTrue()
      .Because("EventStoreOnly should persist to event store");
    await Assert.That(extracted[0].Mode.HasFlag(DispatchMode.LocalDispatch)).IsFalse()
      .Because("EventStoreOnly should NOT invoke local receptors");
  }

  /// <summary>
  /// Verifies that Local mode has BOTH LocalDispatch AND EventStore flags.
  /// </summary>
  [Test]
  public async Task ExtractMessagesWithRouting_Local_HasBothLocalDispatchAndEventStoreAsync() {
    // Arrange
    var evt = new RoutedTestEvent(Guid.NewGuid());
    var routed = Route.Local(evt);

    // Act
    var extracted = MessageExtractor.ExtractMessagesWithRouting(routed).ToList();

    // Assert
    await Assert.That(extracted[0].Mode.HasFlag(DispatchMode.LocalDispatch)).IsTrue()
      .Because("Local should invoke local receptors");
    await Assert.That(extracted[0].Mode.HasFlag(DispatchMode.EventStore)).IsTrue()
      .Because("Local should persist to event store");
  }

  #endregion

  #region Integration Tests - Cascade Behavior

  /// <summary>
  /// Test dispatcher that tracks cascade behavior with routing support.
  /// </summary>
  private sealed class RoutingTestDispatcher : Core.Dispatcher {
    public RoutingTestDispatcher(IServiceProvider serviceProvider)
        : base(serviceProvider, new ServiceInstanceProvider(configuration: null)) {
    }

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      // Handle RoutedTestCommand -> (RoutedTestResult, Routed<RoutedTestEvent>) for Local routing test
      if (messageType == typeof(RoutedTestCommand) && typeof(TResult) == typeof((RoutedTestResult, Routed<RoutedTestEvent>))) {
        return msg => {
          var cmd = (RoutedTestCommand)msg;
          var result = new RoutedTestResult(true);
          var evt = new RoutedTestEvent(cmd.OrderId);
          var routed = Route.Local(evt);
          return ValueTask.FromResult((TResult)(object)(result, routed));
        };
      }
      return null;
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      return null;
    }

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) {
      return evt => {
        RoutedCascadeTracker.TrackLocal(evt!);
        return Task.CompletedTask;
      };
    }

    protected override Func<object, Task>? GetUntypedReceptorPublisher(Type eventType) {
      return evt => {
        RoutedCascadeTracker.TrackLocal(evt);
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
      // Return null to use default cascade behavior (no receptor-level routing override)
      return null;
    }
  }

  /// <summary>
  /// Verifies that cascade correctly routes Local messages to local receptors.
  /// This tests the actual cascade behavior in the Dispatcher.
  /// </summary>
  /// <remarks>
  /// This test will FAIL initially because _cascadeEventsFromResultAsync doesn't use ExtractMessagesWithRouting yet.
  /// After implementing Phase 3, this test should pass.
  /// </remarks>
  [Test]
  [NotInParallel]
  public async Task CascadeFromResult_WithRouteLocal_InvokesLocalReceptorAsync() {
    // Arrange
    RoutedCascadeTracker.Reset();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(new TestServiceScopeFactory(services.BuildServiceProvider()));
    var provider = services.BuildServiceProvider();
    var dispatcher = new RoutingTestDispatcher(provider);
    var command = new RoutedTestCommand(Guid.NewGuid());

    // Act - Invoke and let cascade happen
    var (result, routedEvent) = await dispatcher.LocalInvokeAsync<(RoutedTestResult, Routed<RoutedTestEvent>)>(command);

    // Assert - Result should be returned
    await Assert.That(result.Success).IsTrue();

    // Assert - Local receptor should be invoked for Route.Local
    await Assert.That(RoutedCascadeTracker.LocalCount).IsEqualTo(1)
      .Because("Route.Local should cascade to local receptors");
  }

  #endregion

  #region Test Infrastructure

  /// <summary>
  /// Simple service scope factory for testing.
  /// </summary>
  private sealed class TestServiceScopeFactory : IServiceScopeFactory {
    private readonly IServiceProvider _provider;

    public TestServiceScopeFactory(IServiceProvider provider) {
      _provider = provider;
    }

    public IServiceScope CreateScope() {
      return new TestServiceScope(_provider);
    }
  }

  /// <summary>
  /// Simple service scope for testing.
  /// </summary>
  private sealed class TestServiceScope : IServiceScope {
    public TestServiceScope(IServiceProvider provider) {
      ServiceProvider = provider;
    }

    public IServiceProvider ServiceProvider { get; }

    public void Dispose() { }
  }

  #endregion
}
