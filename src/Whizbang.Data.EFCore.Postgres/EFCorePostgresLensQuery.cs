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
/// Abstract base class for multi-model EF Core lens queries.
/// Provides common dispose pattern and GetByIdAsync implementation.
/// </summary>
/// <docs>lenses/multi-model-queries</docs>
public abstract class EFCorePostgresLensQueryBase : IDisposable, IAsyncDisposable {
  private readonly DbContext _context;
  private readonly IReadOnlyDictionary<Type, string> _tableNames;
  private bool _disposed;

  /// <summary>The EF Core DbContext shared across all queries.</summary>
  protected DbContext Context => _context;

  /// <summary>
  /// Initializes the base lens query with shared context.
  /// </summary>
  protected EFCorePostgresLensQueryBase(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _tableNames = tableNames;
  }

  /// <summary>
  /// Gets the query for a specific model type. Must be implemented by derived classes.
  /// </summary>
  protected abstract IQueryable<PerspectiveRow<T>> GetQueryCore<T>() where T : class;

  /// <summary>
  /// Gets a model by ID using the query for type T.
  /// </summary>
  protected async Task<T?> GetByIdCoreAsync<T>(Guid id, CancellationToken cancellationToken) where T : class {
    var row = await GetQueryCore<T>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  /// <inheritdoc/>
  public void Dispose() {
    if (!_disposed) {
      _context.Dispose();
      _disposed = true;
    }
    GC.SuppressFinalize(this);
  }

  /// <inheritdoc/>
  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _context.DisposeAsync();
      _disposed = true;
    }
    GC.SuppressFinalize(this);
  }
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2}"/> for PostgreSQL.
/// </summary>
/// <docs>lenses/multi-model-queries</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryMultiGenericTests.cs</tests>
public sealed class EFCorePostgresLensQuery<T1, T2> : EFCorePostgresLensQueryBase, ILensQuery<T1, T2>
    where T1 : class
    where T2 : class {

  public EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : base(dbContext, tableNames) { }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => GetQueryCore<T>();

  protected override IQueryable<PerspectiveRow<T>> GetQueryCore<T>() {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid. Valid types: {typeof(T1).Name}, {typeof(T2).Name}");
  }

  public Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class
      => GetByIdCoreAsync<T>(id, cancellationToken);
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3}"/> for PostgreSQL.
/// </summary>
/// <docs>lenses/multi-model-queries</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryMultiGenericTests.cs</tests>
public sealed class EFCorePostgresLensQuery<T1, T2, T3> : EFCorePostgresLensQueryBase, ILensQuery<T1, T2, T3>
    where T1 : class
    where T2 : class
    where T3 : class {

  public EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : base(dbContext, tableNames) { }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => GetQueryCore<T>();

  protected override IQueryable<PerspectiveRow<T>> GetQueryCore<T>() {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid. Valid types: {typeof(T1).Name}, {typeof(T2).Name}, {typeof(T3).Name}");
  }

  public Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class
      => GetByIdCoreAsync<T>(id, cancellationToken);
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4> : EFCorePostgresLensQueryBase, ILensQuery<T1, T2, T3, T4>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class {

  public EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : base(dbContext, tableNames) { }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => GetQueryCore<T>();

  protected override IQueryable<PerspectiveRow<T>> GetQueryCore<T>() {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class
      => GetByIdCoreAsync<T>(id, cancellationToken);
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5> : EFCorePostgresLensQueryBase, ILensQuery<T1, T2, T3, T4, T5>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class {

  public EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : base(dbContext, tableNames) { }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => GetQueryCore<T>();

  protected override IQueryable<PerspectiveRow<T>> GetQueryCore<T>() {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class
      => GetByIdCoreAsync<T>(id, cancellationToken);
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5, T6}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6> : EFCorePostgresLensQueryBase, ILensQuery<T1, T2, T3, T4, T5, T6>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class {

  public EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : base(dbContext, tableNames) { }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => GetQueryCore<T>();

  protected override IQueryable<PerspectiveRow<T>> GetQueryCore<T>() {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T6>>().AsNoTracking();
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class
      => GetByIdCoreAsync<T>(id, cancellationToken);
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5, T6, T7}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7> : EFCorePostgresLensQueryBase, ILensQuery<T1, T2, T3, T4, T5, T6, T7>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class {

  public EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : base(dbContext, tableNames) { }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => GetQueryCore<T>();

  protected override IQueryable<PerspectiveRow<T>> GetQueryCore<T>() {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T6>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T7>>().AsNoTracking();
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class
      => GetByIdCoreAsync<T>(id, cancellationToken);
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5, T6, T7, T8}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7, T8> : EFCorePostgresLensQueryBase, ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class
    where T8 : class {

  public EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : base(dbContext, tableNames) { }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => GetQueryCore<T>();

  protected override IQueryable<PerspectiveRow<T>> GetQueryCore<T>() {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T6>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T7>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T8)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T8>>().AsNoTracking();
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class
      => GetByIdCoreAsync<T>(id, cancellationToken);
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5, T6, T7, T8, T9}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9> : EFCorePostgresLensQueryBase, ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9>
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class
    where T8 : class
    where T9 : class {

  public EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : base(dbContext, tableNames) { }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => GetQueryCore<T>();

  protected override IQueryable<PerspectiveRow<T>> GetQueryCore<T>() {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T6>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T7>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T8)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T8>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T9)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T9>>().AsNoTracking();
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class
      => GetByIdCoreAsync<T>(id, cancellationToken);
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3, T4, T5, T6, T7, T8, T9, T10}"/> for PostgreSQL.
/// </summary>
public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> : EFCorePostgresLensQueryBase, ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
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

  public EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : base(dbContext, tableNames) { }

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => GetQueryCore<T>();

  protected override IQueryable<PerspectiveRow<T>> GetQueryCore<T>() {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T1>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T2>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T3>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T4>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T5>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T6>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T7>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T8)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T8>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T9)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T9>>().AsNoTracking();
    }

    if (typeof(T) == typeof(T10)) {
      return (IQueryable<PerspectiveRow<T>>)(object)Context.Set<PerspectiveRow<T10>>().AsNoTracking();
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid for this ILensQuery.");
  }

  public Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class
      => GetByIdCoreAsync<T>(id, cancellationToken);
}
