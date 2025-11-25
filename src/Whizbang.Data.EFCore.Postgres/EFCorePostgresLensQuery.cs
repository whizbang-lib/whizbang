using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
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
  public EFCorePostgresLensQuery(DbContext context, string tableName) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
    _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
  }

  /// <inheritdoc/>
  public IQueryable<PerspectiveRow<TModel>> Query =>
      _context.Set<PerspectiveRow<TModel>>();

  /// <inheritdoc/>
  public async Task<TModel?> GetByIdAsync(string id, CancellationToken cancellationToken = default) {
    var row = await _context.Set<PerspectiveRow<TModel>>()
        .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    return row?.Data;
  }
}
