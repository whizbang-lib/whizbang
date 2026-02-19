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
}
