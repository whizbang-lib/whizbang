using System.Linq.Expressions;

namespace Whizbang.Core.Perspectives.Hooks;

/// <summary>
/// The fluent surface a per-event apply hook targets to describe what it wants mutated, in terms of the marker
/// type <typeparamref name="TMarker"/> it was registered against. The builder records the calls as a declarative
/// <see cref="ApplyHookOp"/> list; the per-event upsert then interprets that list as object mutation on the
/// loaded row. The collective path extends this surface with <see cref="ICollectiveApplyHookBuilder{TMarker}"/>.
/// </summary>
/// <typeparam name="TMarker">
/// The marker the hook was registered for — a concrete model, a base class, or an interface. Selectors are
/// written against it; because the real model is assignable to <typeparamref name="TMarker"/>, the framework can
/// run the recorded ops against any matching model.
/// </typeparam>
/// <docs>fundamentals/messaging/apply-hooks</docs>
public interface IApplyHookBuilder<TMarker> {
  /// <summary>Assign a model data field to <paramref name="value"/>.</summary>
  /// <typeparam name="TProp">The property type.</typeparam>
  /// <param name="selector">A top-level property selector (<c>m =&gt; m.Prop</c>).</param>
  /// <param name="value">The value to assign.</param>
  /// <returns>The same builder for chaining.</returns>
  IApplyHookBuilder<TMarker> SetProperty<TProp>(Expression<Func<TMarker, TProp>> selector, TProp value);

  /// <summary>Assign a physical store column (see <see cref="ApplyHookColumns"/>).</summary>
  /// <param name="column">The column name.</param>
  /// <param name="value">The value to assign (may be <c>null</c>).</param>
  /// <returns>The same builder for chaining.</returns>
  IApplyHookBuilder<TMarker> SetColumn(string column, object? value);

  /// <summary>Bump the row's optimistic <c>version</c> by one.</summary>
  /// <returns>The same builder for chaining.</returns>
  IApplyHookBuilder<TMarker> BumpVersion();

  /// <summary>Drop a model-field setter added earlier in the apply, by property. Collective-focused.</summary>
  /// <typeparam name="TProp">The property type.</typeparam>
  /// <param name="selector">A top-level property selector for the setter to remove.</param>
  /// <returns>The same builder for chaining.</returns>
  IApplyHookBuilder<TMarker> RemoveSetter<TProp>(Expression<Func<TMarker, TProp>> selector);
}

/// <summary>
/// The collective apply-hook builder: the shared <see cref="IApplyHookBuilder{TMarker}"/> verbs plus the two
/// cohort-<c>WHERE</c> verbs that only make sense for a set-based UPDATE (<see cref="AndWhere"/> /
/// <see cref="ReplaceWhere"/>). A per-event apply is a single known row, so those verbs are absent there.
/// </summary>
/// <typeparam name="TMarker">The marker the hook was registered for.</typeparam>
/// <docs>fundamentals/messaging/apply-hooks</docs>
public interface ICollectiveApplyHookBuilder<TMarker> : IApplyHookBuilder<TMarker> {
  /// <summary>AND an extra predicate onto the collective cohort <c>WHERE</c>.</summary>
  /// <param name="predicate">A predicate over the model (<c>m =&gt; …</c>).</param>
  /// <returns>The same builder for chaining.</returns>
  ICollectiveApplyHookBuilder<TMarker> AndWhere(Expression<Func<TMarker, bool>> predicate);

  /// <summary>
  /// Replace the collective cohort <c>WHERE</c> with this predicate. The mandatory tenant scope envelope is
  /// still AND-ed on top by the framework, so the cohort can be reshaped but never escapes its scope.
  /// </summary>
  /// <param name="predicate">A predicate over the model (<c>m =&gt; …</c>).</param>
  /// <returns>The same builder for chaining.</returns>
  ICollectiveApplyHookBuilder<TMarker> ReplaceWhere(Expression<Func<TMarker, bool>> predicate);
}
