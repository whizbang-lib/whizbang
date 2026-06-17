using System.Linq.Expressions;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Fluent builder surface that a <see cref="ICollectiveSpec{TModel}"/>
/// expression-tree literal targets. The two <c>SetProperty</c> overloads
/// cover the two value-source shapes a set-based UPDATE supports:
/// constant values and computed-from-current expressions.
/// </summary>
/// <remarks>
/// <para>
/// Consumers don't implement this directly — they construct an
/// <see cref="Expression{TDelegate}"/> like
/// <c>s =&gt; s.SetProperty(j =&gt; j.Status, "Archived")</c>. The
/// adapter is what owns the runtime implementation; the interface is
/// the structural contract the expression tree binds to.
/// </para>
/// <para>
/// What the surface CAN express (Slice 6 EF adapter + Slice 9 Dapper
/// adapter):
/// </para>
/// <list type="bullet">
///   <item><description>Scalar <c>SetProperty(selector, value)</c> to a constant or event-supplied value.</description></item>
///   <item><description>Scalar <c>SetProperty(selector, computed)</c> to an expression of the row's own current properties — increment, decrement, arithmetic, string concat, date arithmetic, boolean-from-other-property logic.</description></item>
///   <item><description>Multiple <c>SetProperty</c> calls in one spec — composed into a single SQL UPDATE.</description></item>
/// </list>
/// <para>
/// What it CANNOT express (use <c>SpecKind = RawSql</c> via the
/// <see cref="CollectiveApplyForAttribute"/>):
/// </para>
/// <list type="bullet">
///   <item><description>Cross-row aggregates (needs CTE / window function).</description></item>
///   <item><description>Subqueries against other tables.</description></item>
///   <item><description>Conditional updates whose target depends on another column's NEW value (multi-step assignment ordering).</description></item>
///   <item><description>Nested jsonb path manipulation more complex than top-level property assignment.</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveSpecContractTests.cs:ICollectiveSetters_ConstantSetProperty_OverloadCompilesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveSpecContractTests.cs:ICollectiveSetters_ComputedSetProperty_OverloadCompilesAsync</tests>
public interface ICollectiveSetters<TModel> where TModel : class {
  /// <summary>
  /// Assigns <paramref name="selector"/>'s property to a constant
  /// <paramref name="value"/>. Use for "set the new state" patterns
  /// where the value is known at write time and doesn't depend on the
  /// row's current state.
  /// </summary>
  /// <example>
  /// <code>
  /// s.SetProperty(j =&gt; j.Status, JobStatus.Archived);
  /// s.SetProperty(j =&gt; j.ArchivedAt, e.OccurredAt);
  /// </code>
  /// </example>
  ICollectiveSetters<TModel> SetProperty<TProp>(
    Expression<Func<TModel, TProp>> selector, TProp value);

  /// <summary>
  /// Assigns <paramref name="selector"/>'s property to an expression
  /// <paramref name="computed"/> of the row's own current state. Use
  /// for in-place mutation patterns: increment, append, timestamp-touch,
  /// conditional rewrite based on existing values.
  /// </summary>
  /// <example>
  /// <code>
  /// // Increment a counter:
  /// s.SetProperty(j =&gt; j.ViewCount, j =&gt; j.ViewCount + 1);
  ///
  /// // Increment by event-supplied delta (the closure over e.Amount
  /// // is hoisted as a constant in the expression):
  /// s.SetProperty(u =&gt; u.TokenBalance, u =&gt; u.TokenBalance + e.Amount);
  ///
  /// // Compute from current value:
  /// s.SetProperty(x =&gt; x.ExpiresAt, x =&gt; x.ExpiresAt.Date.AddDays(1));
  /// </code>
  /// </example>
  ICollectiveSetters<TModel> SetProperty<TProp>(
    Expression<Func<TModel, TProp>> selector,
    Expression<Func<TModel, TProp>> computed);
}
