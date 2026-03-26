#pragma warning disable CS0618
#pragma warning disable WHIZ400

using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Helper that creates scoped query access for a specific model type within multi-model queries.
/// Uses the shared ScopedAccessHelper for filter building and applies filters per-type.
/// </summary>
internal static class MultiModelScopeHelper {
  internal static IQueryable<PerspectiveRow<T>> GetQuery<T>(
      DbContext context,
      ScopeFilterInfo? filterInfo)
      where T : class {
    var query = context.Set<PerspectiveRow<T>>().AsNoTracking();
    if (filterInfo.HasValue && !filterInfo.Value.IsEmpty) {
      query = ScopedAccessHelper.ApplyFilterInfo(query, filterInfo.Value);
    }
    return query;
  }

  internal static async Task<T?> GetByIdAsync<T>(
      DbContext context,
      ScopeFilterInfo? filterInfo,
      Guid id,
      CancellationToken cancellationToken)
      where T : class {
    var query = GetQuery<T>(context, filterInfo);
    var row = await query.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  internal static ScopeFilterInfo? BuildFilterInfo(
      QueryScope scope,
      IScopeContextAccessor scopeContextAccessor,
      ScopeFilterOverride? overrideValues) {
    var filters = QueryScopeMapper.ToScopeFilter(scope);
    if (filters == ScopeFilter.None) {
      return null;
    }

    var context = scopeContextAccessor.Current
        ?? throw new InvalidOperationException(
            $"Scope '{scope}' requires ambient scope context but IScopeContextAccessor.Current is null.");

    IScopeContext effectiveContext = context;
    if (overrideValues.HasValue) {
      effectiveContext = new OverrideScopeContext(context, overrideValues.Value);
    }

    return ScopeFilterBuilder.Build(filters, effectiveContext);
  }
}

// 2-model
internal sealed class MultiModelScopedAccess<T1, T2>(
    DbContext context, QueryScope scope, IScopeContextAccessor accessor, ScopeFilterOverride? overrides)
    : IScopedMultiLensAccess<T1, T2>
    where T1 : class where T2 : class {
  private readonly ScopeFilterInfo? _filterInfo = MultiModelScopeHelper.BuildFilterInfo(scope, accessor, overrides);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T1>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T2>(context, _filterInfo);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid. Valid types: {typeof(T1).Name}, {typeof(T2).Name}");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    if (typeof(T) == typeof(T1)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T1>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T2)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T2>(context, _filterInfo, id, cancellationToken);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }
}

// 3-model
internal sealed class MultiModelScopedAccess<T1, T2, T3>(
    DbContext context, QueryScope scope, IScopeContextAccessor accessor, ScopeFilterOverride? overrides)
    : IScopedMultiLensAccess<T1, T2, T3>
    where T1 : class where T2 : class where T3 : class {
  private readonly ScopeFilterInfo? _filterInfo = MultiModelScopeHelper.BuildFilterInfo(scope, accessor, overrides);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T1>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T2>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T3>(context, _filterInfo);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    if (typeof(T) == typeof(T1)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T1>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T2)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T2>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T3)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T3>(context, _filterInfo, id, cancellationToken);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }
}

// 4-model
internal sealed class MultiModelScopedAccess<T1, T2, T3, T4>(
    DbContext context, QueryScope scope, IScopeContextAccessor accessor, ScopeFilterOverride? overrides)
    : IScopedMultiLensAccess<T1, T2, T3, T4>
    where T1 : class where T2 : class where T3 : class where T4 : class {
  private readonly ScopeFilterInfo? _filterInfo = MultiModelScopeHelper.BuildFilterInfo(scope, accessor, overrides);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T1>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T2>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T3>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T4>(context, _filterInfo);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    if (typeof(T) == typeof(T1)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T1>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T2)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T2>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T3)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T3>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T4)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T4>(context, _filterInfo, id, cancellationToken);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }
}

// 5-model
internal sealed class MultiModelScopedAccess<T1, T2, T3, T4, T5>(
    DbContext context, QueryScope scope, IScopeContextAccessor accessor, ScopeFilterOverride? overrides)
    : IScopedMultiLensAccess<T1, T2, T3, T4, T5>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class {
  private readonly ScopeFilterInfo? _filterInfo = MultiModelScopeHelper.BuildFilterInfo(scope, accessor, overrides);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T1>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T2>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T3>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T4>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T5>(context, _filterInfo);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    if (typeof(T) == typeof(T1)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T1>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T2)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T2>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T3)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T3>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T4)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T4>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T5)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T5>(context, _filterInfo, id, cancellationToken);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }
}

// 6-model
internal sealed class MultiModelScopedAccess<T1, T2, T3, T4, T5, T6>(
    DbContext context, QueryScope scope, IScopeContextAccessor accessor, ScopeFilterOverride? overrides)
    : IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class {
  private readonly ScopeFilterInfo? _filterInfo = MultiModelScopeHelper.BuildFilterInfo(scope, accessor, overrides);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T1>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T2>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T3>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T4>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T5>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T6>(context, _filterInfo);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    if (typeof(T) == typeof(T1)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T1>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T2)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T2>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T3)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T3>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T4)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T4>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T5)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T5>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T6)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T6>(context, _filterInfo, id, cancellationToken);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }
}

// 7-model
internal sealed class MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7>(
    DbContext context, QueryScope scope, IScopeContextAccessor accessor, ScopeFilterOverride? overrides)
    : IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class {
  private readonly ScopeFilterInfo? _filterInfo = MultiModelScopeHelper.BuildFilterInfo(scope, accessor, overrides);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T1>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T2>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T3>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T4>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T5>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T6>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T7>(context, _filterInfo);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    if (typeof(T) == typeof(T1)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T1>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T2)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T2>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T3)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T3>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T4)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T4>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T5)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T5>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T6)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T6>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T7)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T7>(context, _filterInfo, id, cancellationToken);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }
}

// 8-model
internal sealed class MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8>(
    DbContext context, QueryScope scope, IScopeContextAccessor accessor, ScopeFilterOverride? overrides)
    : IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class where T8 : class {
  private readonly ScopeFilterInfo? _filterInfo = MultiModelScopeHelper.BuildFilterInfo(scope, accessor, overrides);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T1>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T2>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T3>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T4>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T5>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T6>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T7>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T8)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T8>(context, _filterInfo);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    if (typeof(T) == typeof(T1)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T1>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T2)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T2>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T3)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T3>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T4)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T4>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T5)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T5>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T6)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T6>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T7)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T7>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T8)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T8>(context, _filterInfo, id, cancellationToken);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }
}

// 9-model
internal sealed class MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9>(
    DbContext context, QueryScope scope, IScopeContextAccessor accessor, ScopeFilterOverride? overrides)
    : IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class where T8 : class where T9 : class {
  private readonly ScopeFilterInfo? _filterInfo = MultiModelScopeHelper.BuildFilterInfo(scope, accessor, overrides);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T1>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T2>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T3>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T4>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T5>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T6>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T7>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T8)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T8>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T9)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T9>(context, _filterInfo);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    if (typeof(T) == typeof(T1)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T1>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T2)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T2>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T3)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T3>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T4)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T4>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T5)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T5>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T6)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T6>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T7)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T7>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T8)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T8>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T9)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T9>(context, _filterInfo, id, cancellationToken);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }
}

// 10-model
internal sealed class MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(
    DbContext context, QueryScope scope, IScopeContextAccessor accessor, ScopeFilterOverride? overrides)
    : IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class where T8 : class where T9 : class where T10 : class {
  private readonly ScopeFilterInfo? _filterInfo = MultiModelScopeHelper.BuildFilterInfo(scope, accessor, overrides);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class {
    if (typeof(T) == typeof(T1)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T1>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T2)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T2>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T3)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T3>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T4)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T4>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T5)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T5>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T6)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T6>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T7)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T7>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T8)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T8>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T9)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T9>(context, _filterInfo);
    }

    if (typeof(T) == typeof(T10)) {
      return (IQueryable<PerspectiveRow<T>>)(object)MultiModelScopeHelper.GetQuery<T10>(context, _filterInfo);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class {
    if (typeof(T) == typeof(T1)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T1>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T2)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T2>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T3)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T3>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T4)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T4>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T5)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T5>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T6)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T6>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T7)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T7>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T8)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T8>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T9)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T9>(context, _filterInfo, id, cancellationToken);
    }

    if (typeof(T) == typeof(T10)) {
      return (T?)(object?)await MultiModelScopeHelper.GetByIdAsync<T10>(context, _filterInfo, id, cancellationToken);
    }

    throw new ArgumentException($"Type '{typeof(T).Name}' is not valid.");
  }
}
