// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// The receptor registry over every contributed <see cref="IReceptorRegistry"/>: the registry a host whose receptors
/// are declared in more than one assembly serves (#2197).
/// </summary>
/// <remarks>
/// <para>
/// A lookup returns every contribution's receptors for the message and stage, the contributions in the order given
/// (the registration hands them over latest registered first). When two contributions both answer with a receptor
/// of the same id (the same receptor class name declared in two assemblies), the first one given answers and the
/// other is left out: the latest registration wins, the rule every per-assembly registration follows, and startup
/// names the collision (<see cref="RegistrationCollisions"/>). With one contribution, every call goes straight to it.
/// </para>
/// <para>
/// <strong>Runtime registrations go to the latest contribution.</strong> <see cref="Register{TMessage}"/> and
/// <see cref="Unregister{TMessage}"/> (and their response forms) all go to the first contribution given, and the
/// runtime queries (<see cref="HasRuntimeConsumerFor"/>, <see cref="HasAnyRuntimeReceptors"/>,
/// <see cref="GetRuntimeReceptorsForBaseTypesOf"/>) ask it alone. One registry holds every runtime receptor, so a
/// receptor registered once is found once and unregistered from the place it was registered, and the lookups above
/// return it with that registry's own receptors. Any contribution would do; the latest is the one the rule already
/// names, so the choice needs no second rule.
/// </para>
/// </remarks>
/// <docs>fundamentals/receptors/lifecycle-receptors#several-assemblies</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorRegistryContributionTests.cs</tests>
public sealed class CompositeReceptorRegistry : IReceptorRegistry {
  private readonly IReceptorRegistry[] _registries;
  private readonly IReceptorRegistry _runtimeHome;

  /// <summary>Combines the given registries; the first one given holds the runtime registrations.</summary>
  /// <param name="registries">The contributions, latest registered first; at least one.</param>
  public CompositeReceptorRegistry(IEnumerable<IReceptorRegistry> registries) {
    ArgumentNullException.ThrowIfNull(registries);
    _registries = [.. registries];
    if (_registries.Length == 0) {
      throw new ArgumentException("A composite receptor registry needs at least one contribution.", nameof(registries));
    }
    _runtimeHome = _registries[0];
  }

  /// <inheritdoc />
  public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
    _registries.Length == 1
      ? _runtimeHome.GetReceptorsFor(messageType, stage)
      : _latestFirst(messageType, stage);

  /// <inheritdoc />
  public IReadOnlyList<ReceptorInfo> GetRuntimeReceptorsForBaseTypesOf(Type messageType, LifecycleStage stage) =>
    _runtimeHome.GetRuntimeReceptorsForBaseTypesOf(messageType, stage);

  /// <inheritdoc />
  public IReadOnlyCollection<string> GetCompiledReceptorIds() =>
    [.. _registries.SelectMany(registry => registry.GetCompiledReceptorIds()).Distinct(StringComparer.Ordinal)];

  /// <inheritdoc />
  public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
    _runtimeHome.Register(receptor, stage);

  /// <inheritdoc />
  public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
    _runtimeHome.Register(receptor, stage);

  /// <inheritdoc />
  public bool HasRuntimeConsumerFor(string clrTypeName) => _runtimeHome.HasRuntimeConsumerFor(clrTypeName);

  /// <inheritdoc />
  public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
    _runtimeHome.Unregister(receptor, stage);

  /// <inheritdoc />
  public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
    _runtimeHome.Unregister(receptor, stage);

  /// <inheritdoc />
  public bool HasAnyRuntimeReceptors(string messageType) => _runtimeHome.HasAnyRuntimeReceptors(messageType);

  private ReceptorInfo[] _latestFirst(Type messageType, LifecycleStage stage) {
    // Dropped only across contributions: within one registry two receptors may share an id (one class registered
    // at run time for two instances, say), and that registry's answer stands as it gave it.
    var answered = new HashSet<string>(StringComparer.Ordinal);
    var receptors = new List<ReceptorInfo>();
    foreach (var registry in _registries) {
      var found = registry.GetReceptorsFor(messageType, stage).Where(receptor => !answered.Contains(receptor.ReceptorId)).ToList();
      receptors.AddRange(found);
      answered.UnionWith(found.Select(receptor => receptor.ReceptorId));
    }
    return [.. receptors];
  }
}
