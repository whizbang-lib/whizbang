using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IScopedEventStoreQuery and IEventStoreQueryFactory interface definitions.
/// Validates interface contracts for singleton service support.
/// </summary>
/// <docs>core-concepts/event-store-query</docs>
[Category("EventStoreQuery")]
public class IScopedEventStoreQueryTests {
  // === IScopedEventStoreQuery Tests ===

  [Test]
  public async Task IScopedEventStoreQuery_IsInterfaceAsync() {
    await Assert.That(typeof(IScopedEventStoreQuery).IsInterface).IsTrue();
  }

  [Test]
  public async Task IScopedEventStoreQuery_HasQueryAsyncMethodAsync() {
    // Assert - QueryAsync for streaming results
    var method = typeof(IScopedEventStoreQuery).GetMethod("QueryAsync");
    await Assert.That(method).IsNotNull();

    var parameters = method!.GetParameters();
    await Assert.That(parameters.Length).IsEqualTo(2);
    await Assert.That(parameters[0].ParameterType).IsEqualTo(
        typeof(Func<IEventStoreQuery, IQueryable<EventStoreRecord>>));
    await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));

    // Returns IAsyncEnumerable<EventStoreRecord>
    await Assert.That(method.ReturnType).IsEqualTo(typeof(IAsyncEnumerable<EventStoreRecord>));
  }

  [Test]
  public async Task IScopedEventStoreQuery_HasExecuteAsyncMethodAsync() {
    // Assert - ExecuteAsync for materialized queries
    var methods = typeof(IScopedEventStoreQuery).GetMethods()
        .Where(m => m.Name == "ExecuteAsync")
        .ToList();
    await Assert.That(methods.Count).IsGreaterThanOrEqualTo(1);

    // Find the generic version
    var genericMethod = methods.FirstOrDefault(m => m.IsGenericMethod);
    await Assert.That(genericMethod).IsNotNull();
    await Assert.That(genericMethod!.GetGenericArguments().Length).IsEqualTo(1);
  }

  [Test]
  public async Task IScopedEventStoreQuery_QueryAsyncHasCancellationTokenDefaultAsync() {
    // Assert - CancellationToken parameter has default value
    var method = typeof(IScopedEventStoreQuery).GetMethod("QueryAsync");
    await Assert.That(method).IsNotNull();

    var ctParam = method!.GetParameters().FirstOrDefault(p => p.ParameterType == typeof(CancellationToken));
    await Assert.That(ctParam).IsNotNull();
    await Assert.That(ctParam!.HasDefaultValue).IsTrue();
  }

  // === IEventStoreQueryFactory Tests ===

  [Test]
  public async Task IEventStoreQueryFactory_IsInterfaceAsync() {
    await Assert.That(typeof(IEventStoreQueryFactory).IsInterface).IsTrue();
  }

  [Test]
  public async Task IEventStoreQueryFactory_HasCreateScopedMethodAsync() {
    // Assert - CreateScoped returns EventStoreQueryScope
    var method = typeof(IEventStoreQueryFactory).GetMethod("CreateScoped");
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.GetParameters().Length).IsEqualTo(0);
    await Assert.That(method.ReturnType).IsEqualTo(typeof(EventStoreQueryScope));
  }

  // === EventStoreQueryScope Tests ===

  [Test]
  public async Task EventStoreQueryScope_IsSealedClassAsync() {
    await Assert.That(typeof(EventStoreQueryScope).IsClass).IsTrue();
    await Assert.That(typeof(EventStoreQueryScope).IsSealed).IsTrue();
  }

  [Test]
  public async Task EventStoreQueryScope_ImplementsIDisposableAsync() {
    await Assert.That(typeof(EventStoreQueryScope).GetInterfaces())
        .Contains(typeof(IDisposable));
  }

  [Test]
  public async Task EventStoreQueryScope_HasValuePropertyAsync() {
    var valueProperty = typeof(EventStoreQueryScope).GetProperty("Value");
    await Assert.That(valueProperty).IsNotNull();
    await Assert.That(valueProperty!.PropertyType).IsEqualTo(typeof(IEventStoreQuery));
  }

  [Test]
  public async Task EventStoreQueryScope_HasDisposeMethodAsync() {
    var disposeMethod = typeof(EventStoreQueryScope).GetMethod("Dispose");
    await Assert.That(disposeMethod).IsNotNull();
    await Assert.That(disposeMethod!.GetParameters().Length).IsEqualTo(0);
  }

  // === EventStoreQueryScope Instance Tests ===

  [Test]
  public async Task EventStoreQueryScope_Constructor_NullScope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var mockQuery = new MockEventStoreQuery();

    // Act & Assert
    await Assert.That(() => new EventStoreQueryScope(null!, mockQuery))
      .ThrowsExactly<ArgumentNullException>()
      .WithMessageContaining("scope");
  }

  [Test]
  public async Task EventStoreQueryScope_Constructor_NullQuery_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();
    var scope = serviceProvider.CreateScope();

    try {
      // Act & Assert
      await Assert.That(() => new EventStoreQueryScope(scope, null!))
        .ThrowsExactly<ArgumentNullException>()
        .WithMessageContaining("eventStoreQuery");
    } finally {
      scope.Dispose();
    }
  }

  [Test]
  public async Task EventStoreQueryScope_Value_ReturnsProvidedQueryAsync() {
    // Arrange
    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();
    var scope = serviceProvider.CreateScope();
    var mockQuery = new MockEventStoreQuery();

    // Act
    using var queryScope = new EventStoreQueryScope(scope, mockQuery);

    // Assert
    await Assert.That(queryScope.Value).IsSameReferenceAs(mockQuery);
  }

  [Test]
  public async Task EventStoreQueryScope_Dispose_SucceedsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();
    var scope = serviceProvider.CreateScope();
    var mockQuery = new MockEventStoreQuery();
    var queryScope = new EventStoreQueryScope(scope, mockQuery);

    // Act & Assert - should not throw
    await Assert.That(() => queryScope.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task EventStoreQueryScope_Dispose_DisposesUnderlyingScopeAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddScoped<DisposableTracker>();
    var serviceProvider = services.BuildServiceProvider();
    var scope = serviceProvider.CreateScope();
    var tracker = scope.ServiceProvider.GetRequiredService<DisposableTracker>();
    var mockQuery = new MockEventStoreQuery();
    var queryScope = new EventStoreQueryScope(scope, mockQuery);

    // Pre-assert
    await Assert.That(tracker.IsDisposed).IsFalse();

    // Act
    queryScope.Dispose();

    // Assert
    await Assert.That(tracker.IsDisposed).IsTrue();
  }

  [Test]
  public async Task EventStoreQueryScope_CanBeUsedWithUsingStatementAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddScoped<DisposableTracker>();
    var serviceProvider = services.BuildServiceProvider();

    DisposableTracker? tracker;
    using (var scope = serviceProvider.CreateScope()) {
      tracker = scope.ServiceProvider.GetRequiredService<DisposableTracker>();
      var mockQuery = new MockEventStoreQuery();

      // Act
      using (var queryScope = new EventStoreQueryScope(scope, mockQuery)) {
        await Assert.That(queryScope.Value).IsNotNull();
        await Assert.That(tracker.IsDisposed).IsFalse();
      }

      // Assert - scope is disposed after using block
      await Assert.That(tracker.IsDisposed).IsTrue();
    }
  }

  // Helper class to track disposal
  private sealed class DisposableTracker : IDisposable {
    public bool IsDisposed { get; private set; }

    public void Dispose() {
      IsDisposed = true;
    }
  }

  // Mock implementation of IEventStoreQuery for testing
  private sealed class MockEventStoreQuery : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query => Array.Empty<EventStoreRecord>().AsQueryable();
    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query;
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query;
  }
}
