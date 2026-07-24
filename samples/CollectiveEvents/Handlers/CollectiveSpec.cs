using System.Linq.Expressions;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace CollectiveEvents.Sample.Handlers;

/// <summary>
/// Minimal concrete <see cref="ICollectiveSpec{TModel}"/> the sample
/// handlers construct. Whizbang doesn't ship a built-in helper because
/// projects vary in how they like to wrap the spec (some prefer a
/// shared base class with the resolver pre-wired; others build it
/// inline). For the sample we keep it to the smallest record that
/// satisfies the contract.
/// </summary>
/// <remarks>
/// The optional <see cref="Where"/> is the handler's per-model projection of the cohort onto its own
/// columns (defaults to <c>null</c> → the resolver scope filter alone). See
/// <see cref="CollectiveWhereComposer"/> for how it composes with the scope filter per
/// <see cref="CollectiveScopeHandling"/>.
/// </remarks>
internal sealed record CollectiveSpec<TModel>(
  Expression<Action<ICollectiveSetters<TModel>>> Setters,
  Expression<Func<PerspectiveRow<TModel>, bool>>? Where = null
) : ICollectiveSpec<TModel> where TModel : class;
