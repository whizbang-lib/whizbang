// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using System.Reflection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Security;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The <see cref="ICollectiveScopeResolver"/> of fan-outs (#2620): turns a <see cref="FanOutScope"/> into the rows a
/// foreign event targets, <c>row.Data.Key == streamId AND row.Scope.TenantId == tenantId</c>, by the key the model's
/// fan-out of the event declares.
/// </summary>
/// <remarks>
/// <para>
/// The filter is built from the generated key selector (<see cref="PerspectiveFanOutEntry.KeySelector"/>), never from a
/// member looked up by name. Both drivers compile it to <c>"key_column" = @stream AND scope-&gt;&gt;'t' = @tenant</c>,
/// which the key's index serves.
/// </para>
/// <para>
/// The tenant is the event's own: a fan-out targets rows of that tenant only. The sink applies no fan-out of an event
/// that has no tenant.
/// </para>
/// </remarks>
/// <param name="entries">The host's fan-outs (<see cref="PerspectiveFanOutRegistryServiceCollectionExtensions.GetPerspectiveFanOutEntries"/>).</param>
/// <docs>fundamentals/perspectives/multi-stream#how-a-fan-out-applies-live</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:ScopeFilter_MatchesTheRowsWhoseKeyIsTheStream_InTheEventsTenantOnlyAsync</tests>
public sealed class FanOutScopeResolver(IReadOnlyList<PerspectiveFanOutEntry> entries) : ICollectiveScopeResolver {
  // KeyHolds, read from an expression the compiler builds, so the comparison in memory compares what the column stores,
  // as SQL does, with no method looked up by name.
  private static readonly MethodInfo _keyHolds =
    ((MethodCallExpression)((Expression<Func<object?, object?, bool>>)((key, stream) => KeyHolds(key, stream))).Body).Method;

  private readonly IReadOnlyList<PerspectiveFanOutEntry> _entries = entries ?? throw new ArgumentNullException(nameof(entries));

  /// <inheritdoc />
  public string ScopeKind => FanOutScope.KIND;

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:AcceptsPerspective_IsTrueOnlyForAModelThatDeclaresAFanOutAsync</tests>
  public bool AcceptsPerspective<TModel>() where TModel : class => _entries.Any(e => e.ModelType == typeof(TModel));

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:ScopeFilter_ReadsTheKeyTheFanOutForThatEventDeclaresAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:ScopeFilter_ForAModelAndEventNoFanOutDeclares_ThrowsAsync</tests>
  public Expression<Func<PerspectiveRow<TModel>, bool>> ScopeFilter<TModel>(ICollectiveScope scope) where TModel : class {
    var fanOut = _require(scope);
    var entry = _entries.FirstOrDefault(e => e.ModelType == typeof(TModel) && e.EventType == fanOut.EventType)
      ?? throw new InvalidOperationException(
        $"No fan-out of {TypeNameFormatter.ForLog(fanOut.EventType)} to {TypeNameFormatter.ForLog(typeof(TModel))} is registered, so "
        + "its rows cannot be found. The fan-out registry and the collective dispatcher's entries disagree.");
    var key = (Expression<Func<TModel, object?>>)entry.KeySelector;
    var tenantId = fanOut.TenantId;
    Expression<Func<PerspectiveRow<TModel>, bool>> tenant = row => row.Scope.TenantId == tenantId;
    Expression<Func<PerspectiveRow<TModel>, TModel>> data = row => row.Data;
    var row = tenant.Parameters[0];
    var keyOfRow = new Rebind(new Rebind(row).Visit(data.Body)).Visit(key.Body);
    var keyIsStream = Expression.Equal(
      keyOfRow, Expression.Constant(fanOut.StreamId, typeof(object)), liftToNull: false, _keyHolds);
    return Expression.Lambda<Func<PerspectiveRow<TModel>, bool>>(Expression.AndAlso(tenant.Body, keyIsStream), row);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:EnterContext_SetsTheEventsTenant_AndDisposeRestoresThePriorContextAsync</tests>
  public IDisposable EnterContext(ICollectiveScope scope) {
    var fanOut = _require(scope);
    var prior = ScopeContextAccessor.CurrentContext;
    ScopeContextAccessor.CurrentContext = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = fanOut.TenantId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
    };
    return new Restore(prior);
  }

  /// <summary>
  /// Whether a row's key holds the stream id, compared as the key's column stores it: a strongly typed id
  /// (<see cref="IWhizbangId"/>) by its Guid, as its physical column holds it, and any other key by value. The SQL path
  /// compares the column with the stream id the same way.
  /// </summary>
  /// <param name="key">The row's key value, boxed.</param>
  /// <param name="streamId">The stream id, boxed.</param>
  /// <returns><see langword="true"/> when the row is one the event targets.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutCollectiveDispatchTests.cs:ScopeFilter_AStronglyTypedIdKey_MatchesTheRowWhoseIdWrapsTheStreamIdAsync</tests>
  internal static bool KeyHolds(object? key, object? streamId) =>
    Equals(key is IWhizbangId id ? id.ToGuid() : key, streamId);

  private static FanOutScope _require(ICollectiveScope scope) =>
    scope as FanOutScope ?? throw new ArgumentException(
      $"FanOutScopeResolver handles only a FanOutScope (ScopeKind='{FanOutScope.KIND}'); got ScopeKind='{scope?.ScopeKind ?? "null"}'.",
      nameof(scope));

  // Replaces the one parameter of the lambda body it visits: the key selector's row, or the row accessor's.
  private sealed class Rebind(Expression to) : ExpressionVisitor {
    protected override Expression VisitParameter(ParameterExpression node) => to;
  }

  private sealed class Restore(IScopeContext? prior) : IDisposable {
    public void Dispose() => ScopeContextAccessor.CurrentContext = prior;
  }
}
