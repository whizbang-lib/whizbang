// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives the value of a strong id (a <c>[WhizbangId]</c> type) from the handling it is created in (#2604), so a retry
/// of the handling derives the same id instead of minting a second one that nothing else refers to. Every generated strong
/// id's <c>DeriveFrom(handling, purpose)</c> calls it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Inputs:</strong> the handled message (the anchor every dispatch path derives from, see
/// <see cref="EmissionIdentity"/>), the service, the handler (inbox row), the receptor, the id type's pinned id and the
/// purpose. The service keeps two services handling one message apart, the receptor two receptors of one message (owner
/// decision on #2274: two ids). The pinned id, never the type's name, identifies the type, so renaming or moving the type
/// keeps every id it derives.
/// </para>
/// <para>
/// <strong>Layout:</strong> the shared derived layout (see <see cref="DerivedIdentity"/>) at the cause's position: the
/// cause's first 80 bits, then 48 bits of SHA-256 over the UTF-8 canonical string
/// <c>whizbang.strong-id.in-handling.v1\n{cause:N}\n{service}\n{handler}\n{receptor}\n{pinnedId:D}\n{purpose}</c>: the
/// ordinal field takes the digest's bits 40..51 and the hash field its bits 0..3 and 8..39. A strong id has a purpose, not
/// a position among its siblings, so the ordinal field carries hash bits: twelve more bits separate the ids one handling
/// derives for many purposes (a purpose per line of a bulk command), so a collision needs about 2^48 such pairs, not 2^36.
/// The id stays a version 7 id that sorts with its handling; nothing orders a stream by id (#2379).
/// </para>
/// <para>
/// <strong>One id per purpose:</strong> a second derivation for the same type and purpose in one handling throws. Deriving
/// the same purpose in a loop would give every item one id and silently merge them; the purpose must name the item.
/// </para>
/// <para>
/// The value is a compatibility contract: a stored strong id is referenced by every record that names it, so changing
/// the layout changes the id a retry across the deploy derives.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#derived-ids</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/StrongIdDerivationTests.cs</tests>
public static class StrongIdDerivation {
  private const string PREFIX = "whizbang.strong-id.in-handling.v1";

  /// <summary>
  /// Derives the id the strong id pinned as <paramref name="pinnedId"/> takes for <paramref name="purpose"/> in
  /// <paramref name="handling"/>.
  /// </summary>
  /// <param name="handling">The handling the id is created in, usually <see cref="Handling.Current"/>.</param>
  /// <param name="pinnedId">The id type's pinned id, a GUID string.</param>
  /// <param name="purpose">What the id is for within the handling; one id per purpose.</param>
  /// <param name="idTypeName">The id type's name, for the messages of the exceptions only: it takes no part in the id.</param>
  /// <returns>A version 7 id, identical for an identical handled message, service, handler, receptor, pin and purpose.</returns>
  /// <exception cref="ArgumentNullException">An argument is null.</exception>
  /// <exception cref="ArgumentException">The purpose is empty or white space, or the pinned id is not a GUID.</exception>
  /// <exception cref="InvalidOperationException">
  /// The handling is not active (outside a receptor body, a receptor reached by local dispatch outside any handling, or
  /// ended), its message has no id, or the purpose was already
  /// derived for this type in this handling.
  /// </exception>
  public static Guid Derive(Handling handling, string pinnedId, string purpose, string idTypeName) {
    ArgumentNullException.ThrowIfNull(handling);
    ArgumentNullException.ThrowIfNull(pinnedId);
    ArgumentNullException.ThrowIfNull(idTypeName);
    ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
    if (!Guid.TryParse(pinnedId, out var pin)) {
      throw new ArgumentException($"The pinned id of {idTypeName} must be a GUID; '{pinnedId}' is not.", nameof(pinnedId));
    }

    var envelope = handling.Envelope ?? throw new InvalidOperationException(
      $"{idTypeName}.DeriveFrom was called outside a receptor handling: there is no handled message to derive the id from, " +
      "so a retry could not derive the same id again. A receptor called through LocalInvokeAsync, SendAsync or a local publish " +
      "outside any handling has none either: it handles a fresh in-process call, not a delivered message (#2633). Call it in a " +
      "receptor the receptor invoker runs (an inbox or lifecycle-stage receptor) or in one it invokes locally, or mint a fresh " +
      $"id with {idTypeName}.New().");
    if (handling.HasEnded) {
      throw new InvalidOperationException(
        $"{idTypeName}.DeriveFrom was given a handling that has ended: its receptor body has returned, so a retry of the " +
        "handling would not derive this id. Derive the id inside the body, before it returns.");
    }
    var cause = ReceptorHandling.AnchorOf(envelope);
    if (cause == Guid.Empty) {
      throw new InvalidOperationException(
        $"{idTypeName}.DeriveFrom cannot derive from this handling: its message has no id to derive from.");
    }
    if (!handling.TryClaim(pin, purpose)) {
      throw new InvalidOperationException(
        $"{idTypeName}.DeriveFrom was already called for purpose '{purpose}' in this handling: one id per purpose. Keep the id " +
        "the first call returned, or give each id its own purpose (for an item of a collection, name the item in the purpose).");
    }

    var canonical = string.Concat(
      PREFIX, "\n",
      cause.ToString("N"), "\n",
      handling.ServiceName, "\n",
      envelope.DispatchContext.HandlerName ?? string.Empty, "\n",
      handling.ReceptorId ?? string.Empty, "\n",
      pin.ToString("D"), "\n",
      purpose);
    Span<byte> digest = stackalloc byte[32];
    SHA256.HashData(Encoding.UTF8.GetBytes(canonical), digest);
    var ordinalField = (digest[5] << 4) | (digest[6] >> 4);
    return DerivedIdentity.FromCanonical(cause, ordinalField, canonical);
  }

  /// <summary>
  /// The exception the <c>DeriveFrom</c> of a strong id with no pinned id throws. The call does not compile without a
  /// suppression (WHIZ118), since without a pinned id the derived ids would change with the type's name.
  /// </summary>
  /// <param name="idTypeName">The id type's name.</param>
  /// <exception cref="ArgumentNullException"><paramref name="idTypeName"/> is null.</exception>
  public static NotSupportedException NotPinned(string idTypeName) {
    ArgumentNullException.ThrowIfNull(idTypeName);
    return new NotSupportedException(
      $"{idTypeName}.DeriveFrom needs a pinned id, the input its derived ids are fixed by so a rename keeps them. Pin the " +
      "type with [PinnedId(\"<guid>\")] on its declaration, or [WhizbangId(PinnedId = \"<guid>\")] where it is declared.");
  }
}
