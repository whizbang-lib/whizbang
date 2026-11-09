// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>The registry over every contributed <see cref="IPerspectiveRunnerRegistry"/>.</summary>
/// <docs>fundamentals/perspectives/registry</docs>
public sealed class CompositePerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
  /// <summary>Combines the given registries.</summary>
  /// <param name="registries">The contributions.</param>
  public CompositePerspectiveRunnerRegistry(IEnumerable<IPerspectiveRunnerRegistry> registries) {
    ArgumentNullException.ThrowIfNull(registries);
  }

  /// <inheritdoc />
  public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;

  /// <inheritdoc />
  public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];

  /// <inheritdoc />
  public IReadOnlyList<Type> GetEventTypes() => [];

  /// <inheritdoc />
  public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
}
