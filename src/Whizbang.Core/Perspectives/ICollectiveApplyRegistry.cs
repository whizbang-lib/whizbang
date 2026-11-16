// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The collective apply handlers a host knows: every <c>[CollectiveApplyFor]</c> method, as the
/// <see cref="CollectiveApplyEntry"/> the collective dispatcher and the replay applier walk.
/// </summary>
/// <remarks>
/// Every assembly that declares handlers generates one of these and registers it as its contribution
/// (<see cref="CollectiveApplyRegistryServiceCollectionExtensions.AddCollectiveApplyRegistryContribution{TRegistry}"/>), and
/// the container serves a <see cref="CompositeCollectiveApplyRegistry"/> over all of them, so a host whose handlers live
/// in several assemblies knows every one (#2238). Read it with
/// <see cref="CollectiveApplyRegistryServiceCollectionExtensions.GetCollectiveApplyEntries"/>.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events#di-wiring</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveApplyRegistryContributionTests.cs</tests>
public interface ICollectiveApplyRegistry {
  /// <summary>The handlers. Order carries no meaning beyond the composite's latest-first answer.</summary>
  IReadOnlyList<CollectiveApplyEntry> Entries { get; }
}
