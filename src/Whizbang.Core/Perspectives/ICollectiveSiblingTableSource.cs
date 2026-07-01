namespace Whizbang.Core.Perspectives;

/// <summary>
/// Driver-neutral resolution of a sibling perspective model to its physical table name, used by the shared
/// collective predicate→SQL compiler to emit a correlated <c>EXISTS</c> for a cross-perspective cohort
/// (<c>q.Of&lt;TOther&gt;().Any(...)</c>). Each driver's <see cref="ICollectiveQuery"/> binding also implements
/// this so the compiler can read the table name off the <c>q.Of&lt;TOther&gt;()</c> node already embedded in
/// the handler's <c>Where</c> expression — no driver-specific cast in the compiler.
/// </summary>
/// <remarks>
/// The EF Core binding resolves the table from the <c>DbContext</c> model; the Dapper binding resolves it from
/// the <c>model type → table</c> map supplied at registration. Keeping this off <see cref="ICollectiveQuery"/>
/// itself preserves that interface as the handler-facing fluent surface (<c>q.Of&lt;T&gt;()</c>), while table
/// resolution stays a driver-internal concern the compiler consumes.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public interface ICollectiveSiblingTableSource {
  /// <summary>
  /// The perspective table for a sibling model referenced via <see cref="ICollectiveQuery.Of{TOther}"/>.
  /// Throws when the model has no known table — the cohort cannot be projected without it.
  /// </summary>
  /// <param name="modelType">The sibling read model whose perspective table is needed.</param>
  string TableFor(Type modelType);
}
