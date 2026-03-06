using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

// WHIZ400: Suppress for internal implementation - the runtime checks verify T is valid
#pragma warning disable WHIZ400

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_WhenModelExists_ReturnsModelAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_WhenModelDoesNotExist_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_ReturnsIQueryable_WithCorrectTypeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByDataFields_ReturnsMatchingRowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByMetadataFields_ReturnsMatchingRowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByScopeFields_ReturnsMatchingRowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanProjectAcrossColumns_ReturnsAnonymousTypeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsCombinedFilters_FromAllColumnsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsComplexLinqOperations_WithOrderByAndSkipTakeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync</tests>
/// EF Core implementation of <see cref="ILensQuery{TModel}"/> for PostgreSQL.
/// Provides LINQ-based querying over perspective data with support for filtering and projection
/// across data, metadata, and scope columns.
/// </summary>
/// <typeparam name="TModel">The model type stored in the perspective</typeparam>
public class EFCorePostgresLensQuery<TModel> : ILensQuery<TModel>
    where TModel : class {

  private readonly DbContext _context;
  private readonly string _tableName;

  /// <summary>
  /// Initializes a new instance of <see cref="EFCorePostgresLensQuery{TModel}"/>.
  /// </summary>
  /// <param name="context">The EF Core DbContext</param>
  /// <param name="tableName">The table name for this perspective (for diagnostics/logging)</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync</tests>
  public EFCorePostgresLensQuery(DbContext context, string tableName) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
    _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_ReturnsIQueryable_WithCorrectTypeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByDataFields_ReturnsMatchingRowsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByMetadataFields_ReturnsMatchingRowsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByScopeFields_ReturnsMatchingRowsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanProjectAcrossColumns_ReturnsAnonymousTypeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsCombinedFilters_FromAllColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsComplexLinqOperations_WithOrderByAndSkipTakeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_UsesNoTracking_DoesNotTrackEntitiesAsync</tests>
  public IQueryable<PerspectiveRow<TModel>> Query =>
      _context.Set<PerspectiveRow<TModel>>().AsNoTracking();

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_WhenModelExists_ReturnsModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_WhenModelDoesNotExist_ReturnsNullAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_UsesNoTracking_DoesNotTrackEntityAsync</tests>
  public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    var row = await _context.Set<PerspectiveRow<TModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    return row?.Data;
  }
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2}"/> for PostgreSQL.
/// Provides LINQ-based querying over two perspective types with shared DbContext for joins.
/// AOT-compatible: uses typeof() comparisons which are compile-time constants.
/// </summary>
/// <typeparam name="T1">First model type</typeparam>
/// <typeparam name="T2">Second model type</typeparam>
/// <docs>lenses/multi-model-queries</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryMultiGenericTests.cs</tests>
public sealed class EFCorePostgresLensQuery<T1, T2> : ILensQuery<T1, T2>
    where T1 : class
    where T2 : class {

  private readonly DbContext _context;
  private readonly IReadOnlyDictionary<Type, string> _tableNames;
  private bool _disposed;

  /// <summary>
  /// Initializes a new instance with an existing DbContext.
  /// The DbContext is shared across all Query&lt;T&gt;() calls for join support.
  /// </summary>
  public EFCorePostgresLensQuery(
      DbContext dbContext,
      IReadOnlyDictionary<Type, string> tableNames) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _tableNames = tableNames;
  }

  /// <inheritdoc/>
  /// <remarks>AOT-safe: typeof() comparisons are compile-time operations.</remarks>
  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    // AOT-safe pattern: typeof() is a compile-time operation
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }
    throw new ArgumentException(
        $"Type '{typeof(T).Name}' is not valid for this ILensQuery<{typeof(T1).Name}, {typeof(T2).Name}>. " +
        $"Valid types are: {typeof(T1).Name}, {typeof(T2).Name}");
  }

  /// <inheritdoc/>
  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    var row = await Query<T>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  /// <inheritdoc/>
  public void Dispose() {
    if (!_disposed) {
      _context.Dispose();
      _disposed = true;
    }

  }

  /// <inheritdoc/>
  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _context.DisposeAsync();
      _disposed = true;
    }

  }
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3}"/> for PostgreSQL.
/// </summary>
/// <docs>lenses/multi-model-queries</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryMultiGenericTests.cs</tests>
public sealed class EFCorePostgresLensQuery<T1, T2, T3> : ILensQuery<T1, T2, T3>
    where T1 : class
    where T2 : class
    where T3 : class {

  private readonly DbContext _context;
  private readonly IReadOnlyDictionary<Type, string> _tableNames;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext,
      IReadOnlyDictionary<Type, string> tableNames) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _tableNames = tableNames;
  }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }
    throw new ArgumentException(
        $"Type '{typeof(T).Name}' is not valid for this ILensQuery<{typeof(T1).Name}, {typeof(T2).Name}, {typeof(T3).Name}>. " +
        $"Valid types are: {typeof(T1).Name}, {typeof(T2).Name}, {typeof(T3).Name}");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    var row = await Query<T>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  public void Dispose() {
    if (!_disposed) {
      _context.Dispose();
      _disposed = true;
    }

  }

  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _context.DisposeAsync();
      _disposed = true;
    }

  }
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4> : ILensQuery<T1, T2, T3, T4>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class {

  private readonly DbContext _context;
  private readonly IReadOnlyDictionary<Type, string> _tableNames;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext,
      IReadOnlyDictionary<Type, string> tableNames) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _tableNames = tableNames;
  }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }
    throw new ArgumentException(
        $"Type '{typeof(T).Name}' is not valid for this ILensQuery. " +
        $"Valid types are: {typeof(T1).Name}, {typeof(T2).Name}, {typeof(T3).Name}, {typeof(T4).Name}");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    var row = await Query<T>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  public void Dispose() {
    if (!_disposed) {
      _context.Dispose();
      _disposed = true;
    }

  }

  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _context.DisposeAsync();
      _disposed = true;
    }

  }
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5> : ILensQuery<T1, T2, T3, T4, T5>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class {

  private readonly DbContext _context;
  private readonly IReadOnlyDictionary<Type, string> _tableNames;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext,
      IReadOnlyDictionary<Type, string> tableNames) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _tableNames = tableNames;
  }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }
    throw new ArgumentException(
        $"Type '{typeof(T).Name}' is not valid for this ILensQuery. " +
        $"Valid types are: {typeof(T1).Name}, {typeof(T2).Name}, {typeof(T3).Name}, {typeof(T4).Name}, {typeof(T5).Name}");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    var row = await Query<T>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  public void Dispose() {
    if (!_disposed) {
      _context.Dispose();
      _disposed = true;
    }

  }

  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _context.DisposeAsync();
      _disposed = true;
    }

  }
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5, T6}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6> : ILensQuery<T1, T2, T3, T4, T5, T6>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class {

  private readonly DbContext _context;
  private readonly IReadOnlyDictionary<Type, string> _tableNames;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext,
      IReadOnlyDictionary<Type, string> tableNames) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _tableNames = tableNames;
  }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T6>>().AsNoTracking();
    }
    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    var row = await Query<T>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  public void Dispose() {
    if (!_disposed) {
      _context.Dispose();
      _disposed = true;
    }

  }

  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _context.DisposeAsync();
      _disposed = true;
    }

  }
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5, T6, T7}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7> : ILensQuery<T1, T2, T3, T4, T5, T6, T7>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class {

  private readonly DbContext _context;
  private readonly IReadOnlyDictionary<Type, string> _tableNames;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext,
      IReadOnlyDictionary<Type, string> tableNames) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _tableNames = tableNames;
  }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T6>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T7>>().AsNoTracking();
    }
    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    var row = await Query<T>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  public void Dispose() {
    if (!_disposed) {
      _context.Dispose();
      _disposed = true;
    }

  }

  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _context.DisposeAsync();
      _disposed = true;
    }

  }
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5, T6, T7, T8}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7, T8> : ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class
    where T8 : class {

  private readonly DbContext _context;
  private readonly IReadOnlyDictionary<Type, string> _tableNames;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext,
      IReadOnlyDictionary<Type, string> tableNames) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _tableNames = tableNames;
  }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T6>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T7>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T8)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T8>>().AsNoTracking();
    }
    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    var row = await Query<T>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  public void Dispose() {
    if (!_disposed) {
      _context.Dispose();
      _disposed = true;
    }

  }

  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _context.DisposeAsync();
      _disposed = true;
    }

  }
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5, T6, T7, T8, T9}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9> : ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class
    where T8 : class
    where T9 : class {

  private readonly DbContext _context;
  private readonly IReadOnlyDictionary<Type, string> _tableNames;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext,
      IReadOnlyDictionary<Type, string> tableNames) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _tableNames = tableNames;
  }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T6>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T7>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T8)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T8>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T9)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T9>>().AsNoTracking();
    }
    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    var row = await Query<T>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  public void Dispose() {
    if (!_disposed) {
      _context.Dispose();
      _disposed = true;
    }

  }

  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _context.DisposeAsync();
      _disposed = true;
    }

  }
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5, T6, T7, T8, T9, T10}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> : ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
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

  private readonly DbContext _context;
  private readonly IReadOnlyDictionary<Type, string> _tableNames;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext,
      IReadOnlyDictionary<Type, string> tableNames) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _tableNames = tableNames;
  }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T6>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T7>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T8)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T8>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T9)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T9>>().AsNoTracking();
    }
    if (typeof(T) == typeof(T10)) {
      return (IQueryable<PerspectiveRow<T>>)(object)_context.Set<PerspectiveRow<T10>>().AsNoTracking();
    }
    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    var row = await Query<T>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  public void Dispose() {
    if (!_disposed) {
      _context.Dispose();
      _disposed = true;
    }

  }

  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _context.DisposeAsync();
      _disposed = true;
    }

  }
}
