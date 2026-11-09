// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The registry over every contributed <see cref="IPerspectiveRunnerRegistry"/>: the registry a host with several
/// perspective assemblies serves (#2191).
/// </summary>
/// <remarks>
/// <para>
/// A lookup asks each contribution in registration order and returns the first runner found, so a perspective is
/// found in whichever assembly declares it. Generated registries answer with a switch over their own perspectives,
/// so asking one that does not declare the name costs a string comparison per perspective it does.
/// </para>
/// <para>
/// When two contributions declare a perspective under the same CLR type name, the first contribution answers for it
/// and is the one listed by <see cref="GetRegisteredPerspectives"/>; the later one is never reached. This is the rule
/// <see cref="CompositeEventTypeProvider"/> already applies to an event type two assemblies both know (listed once,
/// at its first position), and it keeps the listing and the lookup describing the same registration. Two assemblies
/// cannot both own a perspective name in any case: the name keys its rows and its cursors, so they would share them.
/// </para>
/// <para>
/// The listing, the event types (through <see cref="CompositeEventTypeProvider"/>) and the lifecycle stages with
/// receptors are each built once, at construction: generated registries are views over compile-time arrays.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/registry</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerRegistryContributionTests.cs:TwoAssemblies_EachAssemblysRunnerIsFound_WhicheverRegisteredLastAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerRegistryContributionTests.cs:SamePerspectiveInTwoAssemblies_TheFirstContributionAnswers_AndIsListedOnceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerRegistryContributionTests.cs:TwoAssemblies_LifecycleStagesWithReceptors_AreTheUnionAsync</tests>
public sealed class CompositePerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
  private readonly IPerspectiveRunnerRegistry[] _registries;
  private readonly IReadOnlyList<PerspectiveRegistrationInfo> _perspectives;
  private readonly CompositeEventTypeProvider _eventTypes;

  /// <summary>Combines the given registries, in the order given.</summary>
  /// <param name="registries">The contributions.</param>
  public CompositePerspectiveRunnerRegistry(IEnumerable<IPerspectiveRunnerRegistry> registries) {
    ArgumentNullException.ThrowIfNull(registries);
    _registries = [.. registries];
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var perspectives = new List<PerspectiveRegistrationInfo>();
    var stages = new HashSet<LifecycleStage>();
    foreach (var registry in _registries) {
      perspectives.AddRange(registry.GetRegisteredPerspectives().Where(p => seen.Add(p.ClrTypeName)));
      stages.UnionWith(registry.LifecycleStagesWithReceptors);
    }
    _perspectives = perspectives;
    LifecycleStagesWithReceptors = stages;
    _eventTypes = new CompositeEventTypeProvider(_registries);
  }

  /// <inheritdoc />
  public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) {
    foreach (var registry in _registries) {
      if (registry.GetRunner(perspectiveName, serviceProvider) is { } runner) {
        return runner;
      }
    }
    return null;
  }

  /// <inheritdoc />
  public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => _perspectives;

  /// <inheritdoc />
  public IReadOnlyList<Type> GetEventTypes() => _eventTypes.GetEventTypes();

  /// <inheritdoc />
  public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; }
}
