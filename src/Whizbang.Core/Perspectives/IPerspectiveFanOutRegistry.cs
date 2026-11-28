// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The fan-outs the host's perspectives declare (<see cref="IPerspectiveFanOutFor{TModel, TEvent}"/>), one entry per
/// (model, foreign event).
/// </summary>
/// <remarks>
/// Every assembly that declares a fan-out generates a registry and contributes it through
/// <see cref="PerspectiveFanOutRegistryServiceCollectionExtensions.AddPerspectiveFanOutRegistryContribution{TRegistry}"/>,
/// called by <c>AddWhizbang()</c> through the assembly's module initializer, so the container serves a
/// <see cref="CompositePerspectiveFanOutRegistry"/> over all of them and the consumer registers nothing by hand. Read the
/// served entries with <see cref="PerspectiveFanOutRegistryServiceCollectionExtensions.GetPerspectiveFanOutEntries"/>.
/// </remarks>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-from-another-stream</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutRegistryTests.cs</tests>
public interface IPerspectiveFanOutRegistry {
  /// <summary>The fan-outs. Order carries no meaning beyond the composite's latest-first answer.</summary>
  IReadOnlyList<PerspectiveFanOutEntry> Entries { get; }
}
