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
/// Same layout as <see cref="EmissionIdentity"/> (UUIDv7-shaped, time prefix inherited from the composite
/// id, the remaining bits from SHA-256 over <c>whizbang.composite-child.v1\n{composite}\n{ordinal}\n{type}</c>).
/// The ordinal is the child's position in the composite as the producer packed it, never its position in
/// the set a given consumer keeps, so two consumers of one composite derive the same id for the same child.
/// A composite that carries its children's original ids (<see cref="Whizbang.Core.Minting.IIdentityPreservingComposite"/>)
/// keeps them; derivation applies only where a fresh id would otherwise be minted.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/composite-events#deterministic-child-ids</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeChildIdentityTests.cs</tests>
public static class CompositeChildIdentity {
  private const string PREFIX = "whizbang.composite-child.v1";

  /// <summary>Derives the child id for the <paramref name="ordinal"/>th inner event of <paramref name="compositeMessageId"/>.</summary>
  /// <param name="compositeMessageId">The composite row's message id. Must not be empty.</param>
  /// <param name="ordinal">Zero-based position of the child within the composite.</param>
  /// <param name="childTypeName">The child's type name as the fan-out renders it.</param>
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
      childTypeName);

    return DerivedIdentity.FromCanonical(compositeMessageId, canonical);
  }
}
