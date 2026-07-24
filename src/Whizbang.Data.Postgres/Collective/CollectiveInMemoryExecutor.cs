using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// Per-<typeparamref name="TModel"/> realization of <see cref="ICollectiveInMemoryExecutor"/>. Closes the
/// generic so the reflection-free <see cref="CollectiveInMemoryEvaluator{TModel}"/> can be invoked from the
/// non-generic replay applier. Driver-neutral (lives in <c>Whizbang.Data.Postgres</c>, shared by the EF Core
/// and Dapper drivers) — the in-memory apply has no SQL, so both drivers use the same executor.
/// </summary>
/// <typeparam name="TModel">The perspective model the collective event mutates.</typeparam>
public sealed class CollectiveInMemoryExecutor<TModel> : ICollectiveInMemoryExecutor
    where TModel : class {

  /// <inheritdoc/>
  public Type ModelType => typeof(TModel);

  /// <inheritdoc/>
  public object ApplyToRow(object spec, object currentModel, Guid streamId) {
    ArgumentNullException.ThrowIfNull(spec);
    ArgumentNullException.ThrowIfNull(currentModel);

    var typedSpec = (ICollectiveSpec<TModel>)spec;
    var model = (TModel)currentModel;

    return CollectiveInMemoryEvaluator<TModel>.Matches(typedSpec, streamId, model)
      ? CollectiveInMemoryEvaluator<TModel>.Apply(typedSpec, model)
      : model;
  }
}
