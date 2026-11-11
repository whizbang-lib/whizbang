// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives the identity of a composite's child from the composite's own id, the child's ordinal within
/// the composite, and the child's type, so that expanding the same composite twice produces the same
/// child rows and the inbox primary key absorbs the repeat instead of storing a second copy.
/// </summary>
/// <remarks>
/// <para>
/// A composite is expanded again whenever its first expansion has not yet committed when the row is
/// re-offered: the claim loop re-emits every leased unprocessed row on every poll, and a second instance
/// takes the row once its lease lapses. With fresh child ids every expansion is a full set of new rows;
/// measured on a bulk import, one composite landed fifteen copies of each inner event. With ids derived
/// from the composite the repeat is a no-op at the primary key.
/// </para>
/// <para>
/// Same layout as <see cref="EmissionIdentity"/> (see <see cref="DerivedIdentity"/>): the composite id's first 80
/// bits, then the child's ordinal, then SHA-256 over <c>whizbang.composite-child.v2\n{composite}\n{ordinal}\n{type}</c> (the type version-free),
/// so children sort in the order the composite lists them and after every child of an earlier composite.
/// The ordinal is the child's position in the composite as the producer packed it, never its position in
/// the set a given consumer keeps, so two consumers of one composite derive the same id for the same child.
/// A composite that carries its children's original ids (<see cref="Whizbang.Core.Minting.IIdentityPreservingComposite"/>)
/// keeps them; derivation applies only where a fresh id would otherwise be minted.
/// </para>
/// <para>
/// <b>One rendering of the type (#2266).</b> Every path derives through <see cref="ForChild(Guid, int, Type)"/> or
/// <see cref="ForChild(Guid, int, string)"/>, and the type name enters the hash version-free
/// (<see cref="EventTypeMatchingHelper.NormalizeTypeName"/>: <c>Namespace.Type, Assembly</c>, generic arguments
/// likewise). The raw expansion reads the type off the wire, which is the publisher's versioned assembly-qualified
/// name; the typed expansion and the publisher's local fan-out have only the local CLR type. A receiver whose contract
/// assembly carries another version rendered another name from its own type and derived a third id for the same child,
/// so its stream digests disagreed with nothing missing. The version is the one part of the name no path can reproduce
/// from what another holds, so it is the part left out. A name the publisher already wrote without it (the framework's
/// stored form) derives the id it always did.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/composite-events#deterministic-child-ids</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeChildIdentityTests.cs</tests>
public static class CompositeChildIdentity {
  private const string PREFIX = "whizbang.composite-child.v2";

  /// <summary>
  /// The child id for the <paramref name="ordinal"/>th inner event of <paramref name="compositeMessageId"/>, from the
  /// child's CLR type: the typed expansion and the publisher's local fan-out (#2266).
  /// </summary>
  /// <param name="compositeMessageId">The composite row's message id. Must not be empty.</param>
  /// <param name="ordinal">Zero-based position of the child within the composite.</param>
  /// <param name="childType">The child's runtime type.</param>
  /// <returns>The id every path derives for this child.</returns>
  /// <exception cref="ArgumentNullException">The type is null.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeChildIdentityCanonicalTests.cs:ForChild_TheTypeAndEveryRenderingOfItsName_DeriveOneIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeChildIdentityCanonicalTests.cs:ForChild_ANullType_ThrowsAsync</tests>
  public static Guid ForChild(Guid compositeMessageId, int ordinal, Type childType) {
    ArgumentNullException.ThrowIfNull(childType);
    return Derive(compositeMessageId, ordinal, TypeNameFormatter.AssemblyQualifiedNameOrDisplay(childType));
  }

  /// <summary>
  /// The child id for the <paramref name="ordinal"/>th inner event of <paramref name="compositeMessageId"/>, from the
  /// child's type name as a bundle carries it on the wire: the raw expansion (#2266).
  /// </summary>
  /// <param name="compositeMessageId">The composite row's message id. Must not be empty.</param>
  /// <param name="ordinal">Zero-based position of the child within the composite.</param>
  /// <param name="wireTypeName">The child's type name, with or without the assembly version decoration.</param>
  /// <returns>The id every path derives for this child.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeChildIdentityCanonicalTests.cs:TryExpand_TheTypedAndTheRawPath_DeriveTheSameIds_WhenThePublisherRanAnotherBuildAsync</tests>
  public static Guid ForChild(Guid compositeMessageId, int ordinal, string wireTypeName) =>
    Derive(compositeMessageId, ordinal, wireTypeName);

  /// <summary>
  /// Derives the child id for the <paramref name="ordinal"/>th inner event of <paramref name="compositeMessageId"/>. The
  /// type name enters the hash version-free, so any rendering of one type derives one id (#2266). Library code calls
  /// <see cref="ForChild(Guid, int, Type)"/> or <see cref="ForChild(Guid, int, string)"/>; a test fails a second call site.
  /// </summary>
  /// <param name="compositeMessageId">The composite row's message id. Must not be empty.</param>
  /// <param name="ordinal">Zero-based position of the child within the composite.</param>
  /// <param name="childTypeName">The child's type name, with or without the assembly version decoration.</param>
  /// <returns>A UUIDv7-shaped id that is identical for identical inputs.</returns>
  /// <exception cref="ArgumentException">The composite id is empty.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The ordinal is negative.</exception>
  /// <exception cref="ArgumentNullException">The type name is null.</exception>
  public static Guid Derive(Guid compositeMessageId, int ordinal, string childTypeName) {
    if (compositeMessageId == Guid.Empty) {
      throw new ArgumentException("A deterministic child id needs the composite it was expanded from.", nameof(compositeMessageId));
    }
    ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
    ArgumentNullException.ThrowIfNull(childTypeName);

    var canonical = string.Concat(
      PREFIX, "\n",
      compositeMessageId.ToString("N"), "\n",
      ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), "\n",
      EventTypeMatchingHelper.NormalizeTypeName(childTypeName));

    return DerivedIdentity.FromCanonical(compositeMessageId, ordinal, canonical);
  }
}
