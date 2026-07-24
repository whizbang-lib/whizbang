using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Auto-scoping event store query implementation that creates a fresh service scope for each operation.
/// Ensures DbContext isolation and prevents stale data when used from singleton services.
/// </summary>
/// <docs>fundamentals/events/event-store-query</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/ScopedEventStoreQueryTests.cs</tests>
public class ScopedEventStoreQuery : IScopedEventStoreQuery {
  private readonly IServiceScopeFactory _scopeFactory;

  /// <summary>
  /// Creates a new auto-scoping event store query.
  /// </summary>
  /// <param name="scopeFactory">Service scope factory for creating scopes per operation.</param>
  public ScopedEventStoreQuery(IServiceScopeFactory scopeFactory) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    _scopeFactory = scopeFactory;
  }

  /// <inheritdoc/>
  public IAsyncEnumerable<EventStoreRecord> QueryAsync(
      Func<IEventStoreQuery, IQueryable<EventStoreRecord>> queryBuilder,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(queryBuilder);
    return _queryAsyncCore(queryBuilder, cancellationToken);
  }

  private async IAsyncEnumerable<EventStoreRecord> _queryAsyncCore(
      Func<IEventStoreQuery, IQueryable<EventStoreRecord>> queryBuilder,
      [EnumeratorCancellation] CancellationToken cancellationToken) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    var eventStoreQuery = scope.ServiceProvider.GetRequiredService<IEventStoreQuery>();

    var query = queryBuilder(eventStoreQuery);

    // Materialize the query within the scope before yielding
    // This ensures we fetch all data while DbContext is still alive
    var results = query.ToList();

    foreach (var row in results) {
      cancellationToken.ThrowIfCancellationRequested();
      yield return row;
    }
  }

  /// <inheritdoc/>
  public async Task<TResult> ExecuteAsync<TResult>(
      Func<IEventStoreQuery, CancellationToken, Task<TResult>> queryExecutor,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(queryExecutor);

    await using var scope = _scopeFactory.CreateAsyncScope();
    var eventStoreQuery = scope.ServiceProvider.GetRequiredService<IEventStoreQuery>();

    return await queryExecutor(eventStoreQuery, cancellationToken);
  }
}

/// <summary>
/// Factory for creating scoped <see cref="IEventStoreQuery"/> instances.
/// Use for batch operations where multiple queries should share one scope (and DbContext).
/// </summary>
/// <docs>fundamentals/events/event-store-query</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/EventStoreQueryFactoryTests.cs</tests>
public class EventStoreQueryFactory : IEventStoreQueryFactory {
  private readonly IServiceScopeFactory _scopeFactory;

  /// <summary>
  /// Creates a new event store query factory.
  /// </summary>
  /// <param name="scopeFactory">Service scope factory for creating scopes.</param>
  public EventStoreQueryFactory(IServiceScopeFactory scopeFactory) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    _scopeFactory = scopeFactory;
  }

  /// <inheritdoc/>
  public EventStoreQueryScope CreateScoped() {
    var scope = _scopeFactory.CreateScope();
    var eventStoreQuery = scope.ServiceProvider.GetRequiredService<IEventStoreQuery>();
    return new EventStoreQueryScope(scope, eventStoreQuery);
  }
}
