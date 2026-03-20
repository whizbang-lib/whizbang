#pragma warning disable S3604, S3928 // Primary constructor field/property initializers are intentional

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Auto-scoping event store query for use in singleton services, background workers, or test fixtures.
/// Each operation creates its own service scope, ensuring fresh DbContext and avoiding stale data.
/// For batch operations requiring multiple queries in one scope, use <see cref="IEventStoreQueryFactory"/>.
/// </summary>
/// <docs>fundamentals/events/event-store-query</docs>
/// <tests>Whizbang.Core.Tests/Messaging/IScopedEventStoreQueryTests.cs</tests>
public interface IScopedEventStoreQuery {
  /// <summary>
  /// Executes a query with auto-created scope.
  /// Returns IAsyncEnumerable for streaming results (scope disposed after enumeration).
  /// </summary>
  /// <param name="queryBuilder">Function that builds the query using the scoped IEventStoreQuery.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Async enumerable of event store records.</returns>
  /// <tests>Whizbang.Core.Tests/Messaging/IScopedEventStoreQueryTests.cs:IScopedEventStoreQuery_HasQueryAsyncMethodAsync</tests>
  IAsyncEnumerable<EventStoreRecord> QueryAsync(
      Func<IEventStoreQuery, IQueryable<EventStoreRecord>> queryBuilder,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Executes a materialized query with auto-created scope.
  /// Use for ToListAsync, FirstOrDefaultAsync, CountAsync, etc.
  /// </summary>
  /// <typeparam name="TResult">The query result type.</typeparam>
  /// <param name="queryExecutor">Function that executes the query and materializes results.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The query result.</returns>
  /// <tests>Whizbang.Core.Tests/Messaging/IScopedEventStoreQueryTests.cs:IScopedEventStoreQuery_HasExecuteAsyncMethodAsync</tests>
  Task<TResult> ExecuteAsync<TResult>(
      Func<IEventStoreQuery, CancellationToken, Task<TResult>> queryExecutor,
      CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory for creating scoped IEventStoreQuery instances.
/// Use for batch operations where multiple queries should share one scope (and DbContext).
/// </summary>
/// <docs>fundamentals/events/event-store-query</docs>
/// <tests>Whizbang.Core.Tests/Messaging/IScopedEventStoreQueryTests.cs</tests>
public interface IEventStoreQueryFactory {
  /// <summary>
  /// Creates a scoped IEventStoreQuery instance.
  /// IMPORTANT: Caller MUST dispose the returned object to release scope.
  /// </summary>
  /// <returns>Disposable wrapper containing the scoped event store query.</returns>
  /// <tests>Whizbang.Core.Tests/Messaging/IScopedEventStoreQueryTests.cs:IEventStoreQueryFactory_HasCreateScopedMethodAsync</tests>
  EventStoreQueryScope CreateScoped();
}

/// <summary>
/// Disposable wrapper for scoped IEventStoreQuery instances.
/// Ensures proper scope and DbContext disposal.
/// </summary>
/// <tests>Whizbang.Core.Tests/Messaging/IScopedEventStoreQueryTests.cs</tests>
/// <remarks>
/// Creates a new event store query scope wrapper.
/// </remarks>
/// <param name="scope">The service scope to manage.</param>
/// <param name="eventStoreQuery">The scoped event store query instance.</param>
public sealed class EventStoreQueryScope(IServiceScope scope, IEventStoreQuery eventStoreQuery) : IDisposable {
  private readonly IServiceScope _scope = scope ?? throw new ArgumentNullException(nameof(scope));

  /// <summary>
  /// The scoped IEventStoreQuery instance.
  /// Valid until Dispose() is called.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Messaging/IScopedEventStoreQueryTests.cs:EventStoreQueryScope_HasValuePropertyAsync</tests>
  public IEventStoreQuery Value { get; } = eventStoreQuery ?? throw new ArgumentNullException(nameof(eventStoreQuery));

  /// <summary>
  /// Disposes the service scope and releases the DbContext.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Messaging/IScopedEventStoreQueryTests.cs:EventStoreQueryScope_HasDisposeMethodAsync</tests>
  public void Dispose() {
    _scope.Dispose();
  }
}
