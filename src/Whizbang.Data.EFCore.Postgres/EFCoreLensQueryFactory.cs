using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of <see cref="ILensQueryFactory"/>.
/// Creates a DbContext from the pool and provides ILensQuery instances that share it.
/// </summary>
/// <remarks>
/// Each factory instance owns its own DbContext from the connection pool.
/// Multiple <see cref="GetQuery{TModel}"/> calls return queries that share the same DbContext,
/// enabling joins across different model types when needed.
///
/// Registered as Transient - each injection gets a fresh factory with its own DbContext.
/// </remarks>
/// <typeparam name="TDbContext">The DbContext type</typeparam>
/// <docs>fundamentals/lenses/lens-query-factory</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/EFCoreLensQueryFactoryTests.cs</tests>
public sealed class EFCoreLensQueryFactory<TDbContext> : ILensQueryFactory
    where TDbContext : DbContext {

  private readonly TDbContext _context;
  private readonly IReadOnlyDictionary<Type, string> _tableNames;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly IOptions<WhizbangCoreOptions> _whizbangOptions;
  private bool _disposed;

  /// <summary>
  /// Creates a new factory instance with a DbContext from the pool.
  /// </summary>
  /// <param name="dbContextFactory">Factory to create DbContext instances from the pool</param>
  /// <param name="tableNames">Dictionary mapping model types to their perspective table names</param>
  /// <param name="scopeContextAccessor">Accessor for ambient scope context</param>
  /// <param name="whizbangOptions">Whizbang core options containing default query scope</param>
  /// <exception cref="ArgumentNullException">When dbContextFactory or tableNames is null</exception>
  public EFCoreLensQueryFactory(
      IDbContextFactory<TDbContext> dbContextFactory,
      IReadOnlyDictionary<Type, string> tableNames,
      IScopeContextAccessor scopeContextAccessor,
      IOptions<WhizbangCoreOptions> whizbangOptions) {
    ArgumentNullException.ThrowIfNull(dbContextFactory);
    ArgumentNullException.ThrowIfNull(tableNames);

    _context = dbContextFactory.CreateDbContext();
    _tableNames = tableNames;
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _whizbangOptions = whizbangOptions ?? throw new ArgumentNullException(nameof(whizbangOptions));
  }

  /// <summary>
  /// Backward-compatible constructor for tests. Uses Global scope (no filtering).
  /// </summary>
  internal EFCoreLensQueryFactory(
      IDbContextFactory<TDbContext> dbContextFactory,
      IReadOnlyDictionary<Type, string> tableNames)
      : this(dbContextFactory, tableNames, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  /// <inheritdoc/>
  /// <exception cref="ObjectDisposedException">When called after the factory is disposed</exception>
  /// <exception cref="KeyNotFoundException">When the model type is not registered</exception>
  public ILensQuery<TModel> GetQuery<TModel>() where TModel : class {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (!_tableNames.TryGetValue(typeof(TModel), out var tableName)) {
      throw new KeyNotFoundException(
          $"No table name registered for model type '{typeof(TModel).Name}'. " +
          "Ensure the model is registered in the ILensQueryFactory's table name dictionary.");
    }

    return new EFCorePostgresLensQuery<TModel>(_context, tableName, _scopeContextAccessor, _whizbangOptions);
  }

  /// <summary>
  /// Disposes the DbContext synchronously. Required for DI container compatibility.
  /// Prefer <see cref="DisposeAsync"/> when possible.
  /// Safe to call multiple times.
  /// </summary>
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
