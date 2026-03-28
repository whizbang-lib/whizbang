namespace Whizbang.Core.Lenses;

#pragma warning disable S2326 // Unused type parameters should be removed
#pragma warning disable S2436 // Reduce the number of type parameters in the generic type
// T1, T2, T3, etc. are intentionally declared at the interface level to document which model types
// are valid for the Query<T>() and GetByIdAsync<T>() methods. The runtime implementation enforces
// that T must be one of the declared types. This pattern enables multi-model queries with shared DbContext.
// Supporting up to 10 model types allows complex multi-table joins while maintaining type safety.

/// <summary>
/// Non-generic marker interface for lens query types.
/// Used by <see cref="IScopedLensFactory"/> to constrain generic type parameters.
/// </summary>
/// <docs>fundamentals/lenses/lenses</docs>
public interface ILensQuery;

/// <summary>
/// Read-only LINQ abstraction for querying perspective data (scoped lifetime).
/// </summary>
/// <typeparam name="TModel">The read model type to query</typeparam>
/// <docs>fundamentals/lenses/lenses</docs>
/// <docs>fundamentals/lenses/scoped-queries</docs>
public interface ILensQuery<TModel> : ILensQuery where TModel : class {
  // === Fluent Scope API ===

  /// <summary>
  /// Select a specific scope for querying.
  /// </summary>
  IScopedLensAccess<TModel> Scope(QueryScope scope);

  /// <summary>
  /// Select a scope with explicit override values instead of ambient context.
  /// </summary>
  IScopedLensAccess<TModel> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues);

  /// <summary>
  /// Access queries using the configured default scope.
  /// </summary>
  IScopedLensAccess<TModel> DefaultScope { get; }

  // === Legacy API ===

  /// <inheritdoc/>
  [Obsolete("Use .DefaultScope.Query or .Scope(QueryScope.X).Query instead.")]
  IQueryable<PerspectiveRow<TModel>> Query { get; }

  /// <inheritdoc/>
  [Obsolete("Use .DefaultScope.GetByIdAsync() or .Scope(QueryScope.X).GetByIdAsync() instead.")]
  Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <inheritdoc/>
public interface ILensQuery<T1, T2> : ILensQuery, IAsyncDisposable, IDisposable
    where T1 : class where T2 : class {
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2> Scope(QueryScope scope);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2> DefaultScope { get; }
  /// <inheritdoc/>
  [Obsolete("Use scope API")] IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
  /// <inheritdoc/>
  [Obsolete("Use scope API")] Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <inheritdoc/>
public interface ILensQuery<T1, T2, T3> : ILensQuery, IAsyncDisposable, IDisposable
    where T1 : class where T2 : class where T3 : class {
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3> Scope(QueryScope scope);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3> DefaultScope { get; }
  /// <inheritdoc/>
  [Obsolete("Use scope API")] IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
  /// <inheritdoc/>
  [Obsolete("Use scope API")] Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <inheritdoc/>
public interface ILensQuery<T1, T2, T3, T4> : ILensQuery, IAsyncDisposable, IDisposable
    where T1 : class where T2 : class where T3 : class where T4 : class {
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4> Scope(QueryScope scope);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4> DefaultScope { get; }
  /// <inheritdoc/>
  [Obsolete("Use scope API")] IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
  /// <inheritdoc/>
  [Obsolete("Use scope API")] Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <inheritdoc/>
public interface ILensQuery<T1, T2, T3, T4, T5> : ILensQuery, IAsyncDisposable, IDisposable
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class {
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5> Scope(QueryScope scope);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5> DefaultScope { get; }
  /// <inheritdoc/>
  [Obsolete("Use scope API")] IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
  /// <inheritdoc/>
  [Obsolete("Use scope API")] Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <inheritdoc/>
public interface ILensQuery<T1, T2, T3, T4, T5, T6> : ILensQuery, IAsyncDisposable, IDisposable
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class {
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6> Scope(QueryScope scope);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6> DefaultScope { get; }
  /// <inheritdoc/>
  [Obsolete("Use scope API")] IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
  /// <inheritdoc/>
  [Obsolete("Use scope API")] Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <inheritdoc/>
public interface ILensQuery<T1, T2, T3, T4, T5, T6, T7> : ILensQuery, IAsyncDisposable, IDisposable
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class {
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7> Scope(QueryScope scope);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7> DefaultScope { get; }
  /// <inheritdoc/>
  [Obsolete("Use scope API")] IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
  /// <inheritdoc/>
  [Obsolete("Use scope API")] Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <inheritdoc/>
public interface ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8> : ILensQuery, IAsyncDisposable, IDisposable
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class where T8 : class {
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8> Scope(QueryScope scope);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8> DefaultScope { get; }
  /// <inheritdoc/>
  [Obsolete("Use scope API")] IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
  /// <inheritdoc/>
  [Obsolete("Use scope API")] Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <inheritdoc/>
public interface ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9> : ILensQuery, IAsyncDisposable, IDisposable
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class where T8 : class where T9 : class {
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9> Scope(QueryScope scope);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9> DefaultScope { get; }
  /// <inheritdoc/>
  [Obsolete("Use scope API")] IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
  /// <inheritdoc/>
  [Obsolete("Use scope API")] Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <inheritdoc/>
public interface ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> : ILensQuery, IAsyncDisposable, IDisposable
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class where T8 : class where T9 : class where T10 : class {
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> Scope(QueryScope scope);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues);
  /// <inheritdoc/>
  IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> DefaultScope { get; }
  /// <inheritdoc/>
  [Obsolete("Use scope API")] IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
  /// <inheritdoc/>
  [Obsolete("Use scope API")] Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}
