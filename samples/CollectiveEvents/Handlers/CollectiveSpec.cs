using System.Linq.Expressions;
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
internal sealed record CollectiveSpec<TModel>(
  Expression<Action<ICollectiveSetters<TModel>>> Setters
) : ICollectiveSpec<TModel> where TModel : class;
