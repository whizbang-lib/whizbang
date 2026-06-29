using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Perspective contract for handling an <see cref="ICollectiveEvent"/>.
/// Returns a mutation spec (<see cref="ICollectiveSpec{TModel}"/>) that
/// the framework executes as a single SQL UPDATE against the projection
/// table — not as N per-row Apply invocations.
/// </summary>
/// <remarks>
/// <para>
/// Sits alongside the existing <see cref="IPerspectiveFor{TModel}"/>
/// family. A perspective implementing both can serve per-row events
/// through <c>Apply(model, evt)</c> and collective events through this
/// method — the source generator (Slice 5) discovers each path via the
/// appropriate attribute.
/// </para>
/// <para>
/// Discovery: handlers are picked up at compile time by
/// <see cref="CollectiveApplyForAttribute"/> on the method, NOT on the
/// type. A single perspective class can declare multiple
/// <c>[CollectiveApplyFor]</c>-attributed methods, each for a different
/// <typeparamref name="TEvent"/>.
/// </para>
/// <para>
/// <strong>Failure semantics:</strong> set-based UPDATE is all-or-nothing
/// per projection table — the SQL either commits or rolls back. No per-row
/// soft-fail (no <see cref="ApplyResult{TModel}"/> equivalent). The target
/// use cases ("remove all jobs," "archive all in tenant T") don't want
/// partial-row outcomes anyway.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The read model the perspective maintains.</typeparam>
/// <typeparam name="TEvent">The collective event this handler responds to. Constrained to <see cref="ICollectiveEvent"/> so the source generator can filter handlers by constraint.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveSpecContractTests.cs:ICollectiveApplyFor_TEventConstraint_IsICollectiveEventAsync</tests>
public interface ICollectiveApplyFor<TModel, TEvent>
    where TModel : class
    where TEvent : ICollectiveEvent {
  /// <summary>
  /// Translate the incoming collective event into a mutation spec the
  /// framework will execute as a single SQL UPDATE. The spec describes
  /// the SET clauses (and an optional per-model <see cref="ICollectiveSpec{TModel}.Where"/>);
  /// the framework composes the scope envelope around it unless the handler
  /// opts out via <c>[CollectiveApplyFor(ScopeHandling = Custom)]</c>.
  /// </summary>
  /// <param name="evt">The collective event being applied.</param>
  /// <param name="query">
  /// Driver-bound query context. Use <see cref="ICollectiveQuery.Of{TOther}"/> inside the spec's
  /// <c>Where</c> to scope by a sibling perspective (a different projection table, same id) — e.g. a cohort
  /// defined by a status that lives on a sibling read model. Ignore it for single-table cohorts.
  /// </param>
  /// <returns>The mutation spec.</returns>
  ICollectiveSpec<TModel> Apply(TEvent evt, ICollectiveQuery query);
}
