// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>The receptor registry over every contributed <see cref="IReceptorRegistry"/>.</summary>
/// <docs>fundamentals/receptors/lifecycle-receptors#several-assemblies</docs>
public sealed class CompositeReceptorRegistry : IReceptorRegistry {
  private readonly IReceptorRegistry _last;

  /// <summary>Combines the given registries.</summary>
  /// <param name="registries">The contributions.</param>
  public CompositeReceptorRegistry(IEnumerable<IReceptorRegistry> registries) {
    ArgumentNullException.ThrowIfNull(registries);
    _last = registries.Last();
  }

  /// <inheritdoc />
  public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => _last.GetReceptorsFor(messageType, stage);

  /// <inheritdoc />
  public IReadOnlyList<ReceptorInfo> GetRuntimeReceptorsForBaseTypesOf(Type messageType, LifecycleStage stage) => _last.GetRuntimeReceptorsForBaseTypesOf(messageType, stage);

  /// <inheritdoc />
  public IReadOnlyCollection<string> GetCompiledReceptorIds() => [];

  /// <inheritdoc />
  public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => _last.Register(receptor, stage);

  /// <inheritdoc />
  public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => _last.Register(receptor, stage);

  /// <inheritdoc />
  public bool HasRuntimeConsumerFor(string clrTypeName) => _last.HasRuntimeConsumerFor(clrTypeName);

  /// <inheritdoc />
  public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => _last.Unregister(receptor, stage);

  /// <inheritdoc />
  public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => _last.Unregister(receptor, stage);

  /// <inheritdoc />
  public bool HasAnyRuntimeReceptors(string messageType) => _last.HasAnyRuntimeReceptors(messageType);
}
