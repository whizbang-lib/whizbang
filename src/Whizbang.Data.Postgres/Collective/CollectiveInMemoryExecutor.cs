// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/FanOutReplayApplierTests.cs:FanOutKeyOf_IsTheStreamTheKeyHolds_AStronglyTypedIdByItsGuidAsync</tests>
  public Guid? FanOutKeyOf(PerspectiveFanOutEntry fanOut, object currentModel) {
    ArgumentNullException.ThrowIfNull(fanOut);
    ArgumentNullException.ThrowIfNull(currentModel);
    return FanOutScopeResolver.StreamOf(CollectiveInMemoryEvaluator<TModel>.ReadKey(fanOut.KeySelector, (TModel)currentModel));
  }
}

/// <summary>
/// The replay applier's <see cref="ICollectiveInMemoryExecutorFactory"/> (#2621): makes the driver-neutral
/// <see cref="CollectiveInMemoryExecutor{TModel}"/> for a model a fan-out targets.
/// </summary>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-on-rebuild-and-rewind</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/FanOutReplayApplierTests.cs:AFanOutModelWithNoRegisteredExecutor_GetsItsReplayTwinFromTheEntryAsync</tests>
internal sealed class CollectiveInMemoryExecutorFactory : ICollectiveInMemoryExecutorFactory {
  /// <summary>The one instance: the factory holds nothing.</summary>
  public static readonly CollectiveInMemoryExecutorFactory Instance = new();

  /// <inheritdoc/>
  public ICollectiveInMemoryExecutor Create<TModel>() where TModel : class => new CollectiveInMemoryExecutor<TModel>();
}
