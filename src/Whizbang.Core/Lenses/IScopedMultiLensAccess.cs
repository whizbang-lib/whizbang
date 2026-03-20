namespace Whizbang.Core.Lenses;

#pragma warning disable S2326 // Unused type parameters should be removed
#pragma warning disable S2436 // Reduce the number of type parameters in the generic type
// Type parameters document which model types are valid for Query<T>() and GetByIdAsync<T>().

/// <summary>
/// Intermediate interface providing scoped access to two perspective models.
/// Returned by multi-model <see cref="ILensQuery{T1,T2}"/> scope methods.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#scoped-multi-lens-access</docs>
public interface IScopedMultiLensAccess<T1, T2>
    where T1 : class
    where T2 : class {
  /// <summary>
  /// Gets queryable for the specified model type with scope filters pre-applied.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2</typeparam>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <summary>
  /// Fast single-item lookup by ID within the applied scope.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2</typeparam>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Intermediate interface providing scoped access to three perspective models.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#scoped-multi-lens-access</docs>
public interface IScopedMultiLensAccess<T1, T2, T3>
    where T1 : class
    where T2 : class
    where T3 : class {
  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.Query{T}"/>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.GetByIdAsync{T}"/>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Intermediate interface providing scoped access to four perspective models.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#scoped-multi-lens-access</docs>
public interface IScopedMultiLensAccess<T1, T2, T3, T4>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class {
  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.Query{T}"/>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.GetByIdAsync{T}"/>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Intermediate interface providing scoped access to five perspective models.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#scoped-multi-lens-access</docs>
public interface IScopedMultiLensAccess<T1, T2, T3, T4, T5>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class {
  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.Query{T}"/>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.GetByIdAsync{T}"/>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Intermediate interface providing scoped access to six perspective models.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#scoped-multi-lens-access</docs>
public interface IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class {
  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.Query{T}"/>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.GetByIdAsync{T}"/>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Intermediate interface providing scoped access to seven perspective models.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#scoped-multi-lens-access</docs>
public interface IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class {
  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.Query{T}"/>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.GetByIdAsync{T}"/>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Intermediate interface providing scoped access to eight perspective models.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#scoped-multi-lens-access</docs>
public interface IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class
    where T8 : class {
  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.Query{T}"/>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.GetByIdAsync{T}"/>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Intermediate interface providing scoped access to nine perspective models.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#scoped-multi-lens-access</docs>
public interface IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class
    where T8 : class
    where T9 : class {
  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.Query{T}"/>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.GetByIdAsync{T}"/>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Intermediate interface providing scoped access to ten perspective models.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#scoped-multi-lens-access</docs>
public interface IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class
    where T8 : class
    where T9 : class
    where T10 : class {
  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.Query{T}"/>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <inheritdoc cref="IScopedMultiLensAccess{T1,T2}.GetByIdAsync{T}"/>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}
