// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// One (ephemeral type, Sourced type) pair a read model applies, by the names the event store stores them under.
/// </summary>
/// <param name="EphemeralType">The ephemeral event type, <c>Ns.Type, Assembly</c>.</param>
/// <param name="DurableType">The Sourced event type, <c>Ns.Type, Assembly</c>.</param>
/// <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/MixedStreamAllowanceTests.cs:Allowed_AnAllowingReadModel_AllowsItsPairsAsync</tests>
public sealed record MixedStreamPair(string EphemeralType, string DurableType);

/// <summary>
/// Which (ephemeral type, Sourced type) pairs may share a stream (#2255): the pairs a read model marked
/// <c>[DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Allow)]</c> applies, less every pair a
/// read model without that opt-in also applies. One read model's opt-in can never break another's rebuild.
/// </summary>
/// <remarks>
/// The generator computes each read model's pairs (<see cref="PerspectiveRegistrationInfo.MixedStreamPairs"/>) with the
/// same ephemeral resolution WHIZ130 uses, and WHIZ139 reports a read model that keeps a pair refused within one
/// compilation. The rule is applied again here, over every registered read model, because read models in several
/// assemblies contribute to the one registry a host serves. The answer feeds the store's mixed-stream refusal, the
/// ephemeral history walk and the rebuild's state-based guard.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/MixedStreamAllowanceTests.cs</tests>
public static class MixedStreamAllowance {
  /// <summary>
  /// The allowed pairs: applied by a read model with <see cref="MixedStreams.Allow"/>, and by no read model without it.
  /// Names are normalized (no version, culture or key token); the result is ordered and has no duplicates.
  /// </summary>
  /// <param name="perspectives">Every registered read model.</param>
  /// <returns>The allowed pairs; empty when no read model allows mixing.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/MixedStreamAllowanceTests.cs:Allowed_ASecondReadModelWithoutTheOptIn_KeepsThePairRefusedAsync</tests>
  public static IReadOnlyList<MixedStreamPair> Allowed(IEnumerable<PerspectiveRegistrationInfo> perspectives) => throw new NotImplementedException("#2255 RED");

  /// <summary>The allowed pairs of a host's registry; empty without one.</summary>
  /// <param name="registry">The host's perspective registry, or null.</param>
  /// <returns>The allowed pairs.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/MixedStreamAllowanceTests.cs:Allowed_WithoutARegistry_IsEmptyAsync</tests>
  public static IReadOnlyList<MixedStreamPair> Allowed(IPerspectiveRunnerRegistry? registry) => throw new NotImplementedException("#2255 RED");

  /// <summary>
  /// The pairs under every name their types are stored under: each type's current name and, through the catalog, every
  /// former name (<see cref="TypeNameFormatter.FormatFormerName"/>), so events written under a former name count too.
  /// </summary>
  /// <param name="pairs">The allowed pairs.</param>
  /// <param name="catalog">The application's message types, or null.</param>
  /// <returns>Each pair once per combination of stored names, ordered.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/MixedStreamAllowanceTests.cs:Stored_AddsEachTypesFormerNames_FromTheCatalogAsync</tests>
  public static IReadOnlyList<MixedStreamPair> Stored(IReadOnlyList<MixedStreamPair> pairs, IMessageTypeCatalog? catalog) => throw new NotImplementedException("#2255 RED");

  /// <summary>
  /// A type's stored names: the name itself and, when the catalog declares the type, its former names in the stored form.
  /// </summary>
  /// <param name="typeName">The type's stored name.</param>
  /// <param name="catalog">The application's message types, or null.</param>
  /// <returns>The names, the given one first.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/MixedStreamAllowanceTests.cs:Stored_AddsEachTypesFormerNames_FromTheCatalogAsync</tests>
  public static IReadOnlyList<string> StoredNames(string typeName, IMessageTypeCatalog? catalog) => throw new NotImplementedException("#2255 RED");

  /// <summary>
  /// The ephemeral types a rebuild of <paramref name="perspective"/> may pass over when it judges a stream state-based:
  /// the ephemeral side of each allowed pair the read model does not apply. A read model that applies only the Sourced
  /// type of an allowed pair rebuilds a mixed stream from its Sourced events as before; one that applies the ephemeral
  /// type (the opted-in read model) still has the stream refused, as any ephemeral read model does.
  /// </summary>
  /// <param name="perspective">The read model being rebuilt, or null when it is not registered.</param>
  /// <param name="allowed">The allowed pairs (<see cref="Allowed(IEnumerable{PerspectiveRegistrationInfo})"/>).</param>
  /// <returns>The ephemeral types, ordered; empty when nothing may be passed over.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/MixedStreamAllowanceTests.cs:IgnoredByRebuild_IsTheEphemeralSide_OfPairsTheReadModelDoesNotApplyAsync</tests>
  public static IReadOnlyList<string> IgnoredByRebuild(PerspectiveRegistrationInfo? perspective, IReadOnlyList<MixedStreamPair> allowed) => throw new NotImplementedException("#2255 RED");
}
