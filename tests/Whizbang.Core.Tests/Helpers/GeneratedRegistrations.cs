// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Registry;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Helpers;

/// <summary>
/// The registrations this assembly's module initializers made in the process-global registries, captured once before
/// any test runs, so a test that clears a registry can put them back.
/// </summary>
/// <remarks>
/// <c>ClearForTesting</c> empties a registry that the generated <c>[ModuleInitializer]</c> fills exactly once per process.
/// A test that clears without restoring disables discovery for every test that runs after it, which shows up only when
/// the scheduler happens to run a dependent test later. Capturing in a test-session hook, rather than in a static field
/// of the clearing class, guarantees the snapshot is taken before any clear.
/// </remarks>
public static class GeneratedRegistrations {
  private static ReceptorRegistryContribution[] _receptors = [];
  private static IMessageTagRegistry[] _tags = [];
  private static IMessageTagHookDispatcher[] _hookDispatchers = [];
  private static (Type Model, StreamGroupMembership Membership)[] _streamGroups = [];

  [Before(TestSession)]
  public static void Capture() {
    _receptors = [.. AssemblyRegistry<ReceptorRegistryContribution>.GetOrderedContributions()];
    _tags = [.. AssemblyRegistry<IMessageTagRegistry>.GetOrderedContributions()];
    _hookDispatchers = [.. AssemblyRegistry<IMessageTagHookDispatcher>.GetOrderedContributions()];
    _streamGroups = [.. PerspectiveStreamGroupRegistry.RegisteredModels()
      .SelectMany(model => PerspectiveStreamGroupRegistry.Resolve(model).Select(membership => (model, membership)))];
  }

  /// <summary>Puts back the generated receptor registrations and drops the query's cached snapshot of them.</summary>
  public static void RestoreReceptors() {
    _restore(_receptors);
    WhizbangReceptorRegistryQuery.ClearCacheForTesting();
  }

  /// <summary>Puts back the generated tag registries and tag hook dispatchers.</summary>
  public static void RestoreTags() {
    _restore(_tags);
    _restore(_hookDispatchers);
  }

  /// <summary>Puts back the generated perspective stream group memberships, and nothing else.</summary>
  public static void RestoreStreamGroups() {
    PerspectiveStreamGroupRegistry.Clear();
    foreach (var (model, membership) in _streamGroups) {
      PerspectiveStreamGroupRegistry.Register(model, membership.Key, membership.Announce, membership.Follow, membership.Bridge);
    }
  }

  // Registered in the captured order; the priorities only order contributions, so the index keeps that order.
  private static void _restore<T>(T[] captured) where T : class {
    AssemblyRegistry<T>.ClearForTesting();
    for (var i = 0; i < captured.Length; i++) {
      AssemblyRegistry<T>.Register(captured[i], priority: i);
    }
  }
}
