using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// EF Core driver adapter for collective-event apply (Slice 6). Takes
/// the perspective's <see cref="ICollectiveSpec{TModel}"/> mutation
/// description, the resolver's scope filter, and the matched stream-id
/// set, and produces a single
/// <see cref="EntityFrameworkQueryableExtensions.ExecuteUpdateAsync{TSource}(IQueryable{TSource}, Expression{Action{Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder{TSource}}}, System.Threading.CancellationToken)"/>
/// call against the perspective table.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline:
/// </para>
/// <list type="number">
///   <item><description>Resolve <c>dbContext.Set&lt;PerspectiveRow&lt;TModel&gt;&gt;()</c>.</description></item>
///   <item><description>Compose <c>Where(scopeFilter)</c> (outer scope envelope from the resolver).</description></item>
///   <item><description>Compose <c>Where(matchedStreamIds.Contains(r.Id))</c> (snapshot membership).</description></item>
///   <item><description>Translate the perspective's <c>ICollectiveSetters</c> spec into <c>UpdateSettersBuilder&lt;PerspectiveRow&lt;TModel&gt;&gt;</c> shape via <see cref="CollectiveSettersRewriter"/>.</description></item>
///   <item><description>Append the audit-pointer write: <c>SetProperty(r =&gt; r.LastCollectiveEventId, eventId)</c>.</description></item>
///   <item><description>Execute via <see cref="EntityFrameworkQueryableExtensions.ExecuteUpdateAsync{TSource}(IQueryable{TSource}, Expression{Action{Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder{TSource}}}, System.Threading.CancellationToken)"/>.</description></item>
/// </list>
/// <para>
/// Returns the count of affected rows so the runner (Slice 7) can log /
/// surface it as a metric.
/// </para>
/// <para>
/// AOT: matches Whizbang.Data.EFCore.Postgres's established pattern of
/// suppressing IL2060/IL3050 — EF Core's query translation pipeline is
/// reflection-based by design and the rest of the repository accepts
/// that tradeoff.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The perspective model whose projection table is mutated.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
[SuppressMessage("AOT", "IL2060:MakeGenericMethod can break functionality when AOT compiling", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2070:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2075:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Adapter is generic over TModel; static factory + execute methods match the pattern of EF Core's own generic-static helpers (e.g. EntityFrameworkQueryableExtensions). Slice 6 keeps the surface flat for the runner (Slice 7) to consume.")]
public sealed class EFCoreCollectiveAdapter<TModel> where TModel : class {
  /// <summary>
  /// Build the final <see cref="IQueryable{T}"/> + <see cref="UpdateSettersBuilder{T}"/>
  /// pair that <c>ExecuteUpdateAsync</c> consumes. Exposed separately
  /// from <see cref="ExecuteAsync"/> so unit tests can inspect the
  /// composition without needing a connected DbContext.
  /// </summary>
  internal static (IQueryable<PerspectiveRow<TModel>> Query,
                   Expression<Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<PerspectiveRow<TModel>>>> Setters)
    BuildCall(
      DbContext dbContext,
      ICollectiveSpec<TModel> spec,
      Expression<Func<PerspectiveRow<TModel>, bool>> scopeFilter,
      IReadOnlyList<Guid> matchedStreamIds,
      Guid collectiveEventId) {

    var ids = matchedStreamIds as Guid[] ?? matchedStreamIds.ToArray();

    var query = dbContext.Set<PerspectiveRow<TModel>>()
      .Where(scopeFilter)
      .Where(r => ids.Contains(r.Id));

    var translatedSetters = CollectiveSettersRewriter.Rewrite(spec.Setters);
    var withAudit = _appendAuditWrite(translatedSetters, collectiveEventId);

    return (query, withAudit);
  }

  /// <summary>
  /// Execute the collective-event mutation as a single SQL UPDATE.
  /// Returns the number of affected rows.
  /// </summary>
  public static Task<int> ExecuteAsync(
      DbContext dbContext,
      ICollectiveSpec<TModel> spec,
      Expression<Func<PerspectiveRow<TModel>, bool>> scopeFilter,
      IReadOnlyList<Guid> matchedStreamIds,
      Guid collectiveEventId,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(spec);
    ArgumentNullException.ThrowIfNull(scopeFilter);
    ArgumentNullException.ThrowIfNull(matchedStreamIds);

    if (matchedStreamIds.Count == 0) {
      // No rows matched at write time — nothing to update. The
      // collective event is still durable in wh_event_store; this just
      // skips the no-op SQL UPDATE.
      return Task.FromResult(0);
    }

    var (query, setters) = BuildCall(dbContext, spec, scopeFilter, matchedStreamIds, collectiveEventId);
    // Compile the rewritten Expression tree to a delegate at the API
    // boundary — EF Core 10's ExecuteUpdateAsync takes Action<...>, not
    // Expression<Action<...>>. The inner property-selector lambdas
    // remain as Expression trees inside the body (passed to
    // UpdateSettersBuilder.SetProperty) so EF Core's query translator
    // still sees them.
    return query.ExecuteUpdateAsync(setters.Compile(), cancellationToken);
  }

  /// <summary>
  /// Chains the audit-pointer write onto the perspective's setter
  /// expression so every affected row's <see cref="PerspectiveRow{TModel}.LastCollectiveEventId"/>
  /// column lands in the same SQL UPDATE statement.
  /// </summary>
  private static Expression<Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<PerspectiveRow<TModel>>>>
    _appendAuditWrite(
      Expression<Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<PerspectiveRow<TModel>>>> existing,
      Guid collectiveEventId) {

    var spcParam = existing.Parameters[0];
    var spcType = spcParam.Type;

    // SetProperty<Guid?>(r => r.LastCollectiveEventId, collectiveEventId).
    // The Slice 7b-α step 1 commit guarantees the property exists.
    var rowParam = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    var auditProperty = typeof(PerspectiveRow<TModel>).GetProperty(nameof(PerspectiveRow<TModel>.LastCollectiveEventId))!;

    var auditSelector = Expression.Lambda<Func<PerspectiveRow<TModel>, Guid?>>(
      Expression.Property(rowParam, auditProperty),
      rowParam);

    // Constant-value SetProperty<Guid?> overload.
    var setPropertyDef = spcType.GetMethods()
      .Where(m => m.Name == "SetProperty" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
      .First(m => {
        var ps = m.GetParameters();
        return !(ps[1].ParameterType.IsGenericType &&
                 ps[1].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>));
      })
      .MakeGenericMethod(typeof(Guid?));

    // Chain the audit call onto the existing body so the resulting
    // lambda reads `s => s.SetProperty(<existing>, <existing>).SetProperty(r => r.LastCollectiveEventId, eventId)`.
    var auditCall = Expression.Call(
      existing.Body,
      setPropertyDef,
      Expression.Quote(auditSelector),
      Expression.Constant((Guid?)collectiveEventId, typeof(Guid?)));

    return Expression.Lambda<Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<PerspectiveRow<TModel>>>>(
      auditCall, spcParam);
  }
}
