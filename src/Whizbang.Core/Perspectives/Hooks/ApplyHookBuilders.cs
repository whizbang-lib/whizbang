using System.Linq.Expressions;

namespace Whizbang.Core.Perspectives.Hooks;

/// <summary>
/// Extracts the top-level property name from a selector lambda (<c>m =&gt; m.Prop</c>), stripping any boxing
/// <c>Convert</c> the compiler inserts. Pure expression-metadata inspection — no reflection over runtime types,
/// so it is trim/AOT-clean.
/// </summary>
internal static class ApplyHookSelector {
  public static string PropertyName(LambdaExpression selector) {
    var body = selector.Body;
    while (body is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
      body = convert.Operand;
    }
    if (body is MemberExpression { Member: { } member }) {
      return member.Name;
    }
    throw new NotSupportedException(
      "Apply-hook SetProperty/RemoveSetter only supports a top-level property selector (m => m.PropertyName). " +
      $"Got expression node kind {body.NodeType}.");
  }
}

/// <summary>
/// The per-event builder: records each verb call as an <see cref="ApplyHookOp"/>. One instance per hook
/// invocation; not thread-safe by design (each apply builds and drains its own).
/// </summary>
internal class ApplyHookBuilder<TMarker> : IApplyHookBuilder<TMarker> {
  public List<ApplyHookOp> Ops { get; } = [];

  public IApplyHookBuilder<TMarker> SetProperty<TProp>(Expression<Func<TMarker, TProp>> selector, TProp value) {
    ArgumentNullException.ThrowIfNull(selector);
    Ops.Add(new SetPropertyOp(selector, ApplyHookSelector.PropertyName(selector), value, typeof(TProp)));
    return this;
  }

  public IApplyHookBuilder<TMarker> SetColumn(string column, object? value) {
    ArgumentException.ThrowIfNullOrWhiteSpace(column);
    Ops.Add(new SetColumnOp(column, value));
    return this;
  }

  public IApplyHookBuilder<TMarker> BumpVersion() {
    Ops.Add(new BumpVersionOp());
    return this;
  }

  public IApplyHookBuilder<TMarker> SuppressActivity() {
    Ops.Add(new SuppressActivityOp());
    return this;
  }

  public IApplyHookBuilder<TMarker> RemoveSetter<TProp>(Expression<Func<TMarker, TProp>> selector) {
    ArgumentNullException.ThrowIfNull(selector);
    Ops.Add(new RemoveSetterOp(ApplyHookSelector.PropertyName(selector)));
    return this;
  }
}

/// <summary>
/// The collective builder: the per-event verbs plus the cohort-<c>WHERE</c> verbs. Inherits the shared verb
/// recording from <see cref="ApplyHookBuilder{TMarker}"/>.
/// </summary>
internal sealed class CollectiveApplyHookBuilder<TMarker> : ApplyHookBuilder<TMarker>, ICollectiveApplyHookBuilder<TMarker> {
  public ICollectiveApplyHookBuilder<TMarker> AndWhere(Expression<Func<TMarker, bool>> predicate) {
    ArgumentNullException.ThrowIfNull(predicate);
    Ops.Add(new AndWhereOp(predicate));
    return this;
  }

  public ICollectiveApplyHookBuilder<TMarker> ReplaceWhere(Expression<Func<TMarker, bool>> predicate) {
    ArgumentNullException.ThrowIfNull(predicate);
    Ops.Add(new ReplaceWhereOp(predicate));
    return this;
  }
}
