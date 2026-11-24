// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The one layout for ids the framework derives instead of mints. A derived id sorts where its source sorts, then by
/// its ordinal within the derivation, and is repeatable: the same inputs always produce the same id.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Layout</strong> (UUIDv7-shaped):
/// </para>
/// <code>
///  bits   0..79   the source id's first 80 bits: its millisecond, and the generator's monotonic counter
///                 (version forced to 7 and variant to 10 when the source is not a version 7 id)
///  bits  80..91   the ordinal within the derivation, saturating at 4095
///  bits  92..127  SHA-256 of the canonical string
/// </code>
/// <para>
/// Events are versioned, claimed and applied in id order. Keeping the source's millisecond alone was not enough:
/// two sources issued in the same millisecond differ only in the counter, so ids that replaced it with hash bits
/// sorted in random order, and two commands sent back to back on one stream could have their events versioned
/// the other way round. Keeping the counter preserves the source order; the ordinal then orders the several ids
/// one source derives (a handler's emissions, a composite's children).
/// </para>
/// <para>
/// Past ordinal 4095 the field saturates: the ids still sort after every lower ordinal and stay unique, because
/// the hash covers the full ordinal, but their order among themselves is the hash's. The hash also separates
/// derivations that share a source and an ordinal (two handlers of one message): 36 bits, so a collision needs
/// about 2^36 such pairs.
/// </para>
/// <para>
/// Used by <see cref="EmissionIdentity"/> (a handler's emissions), <see cref="CompositeChildIdentity"/> (a
/// composite's children) and <see cref="GeneratedStreamIdentity"/> (the stream a message starts, at the message's
/// own position, or at its handling's when the message's id is minted). Each caller owns its canonical prefix, so the derivations never collide.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/DerivedIdentityTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/DerivedIdentityPropertyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/EmissionIdentityTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/CompositeChildIdentityTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/GeneratedStreamIdentityTests.cs</tests>
internal static class DerivedIdentity {
  private const int MAX_ORDINAL_FIELD = 0xFFF;

  /// <summary>
  /// Derives the id that sorts at <paramref name="source"/>'s position, then at <paramref name="ordinal"/>, and
  /// whose remaining bits hash <paramref name="canonical"/>.
  /// </summary>
  internal static Guid FromCanonical(Guid source, int ordinal, string canonical) {
    Span<byte> id = stackalloc byte[16];
    _writeSourcePrefix(source, id);
    var field = Math.Min(ordinal, MAX_ORDINAL_FIELD);
    id[10] = (byte)(field >> 4);
    id[11] = (byte)((field & 0x0F) << 4);
    return _withHash(id, canonical);
  }

  /// <summary>
  /// Derives the id that sorts exactly where <paramref name="source"/> does (its first 92 bits: millisecond, counter
  /// and ordinal) and whose last 36 bits hash <paramref name="canonical"/>: a second id for the same position, such as
  /// the stream a message starts (see <see cref="GeneratedStreamIdentity"/>).
  /// </summary>
  internal static Guid AtTheSamePosition(Guid source, string canonical) {
    Span<byte> id = stackalloc byte[16];
    _writeSourcePrefix(source, id);
    id[11] &= 0xF0;
    return _withHash(id, canonical);
  }

  private static void _writeSourcePrefix(Guid source, Span<byte> id) {
    source.TryWriteBytes(id, bigEndian: true, out _);
    id[6] = (byte)(0x70 | (id[6] & 0x0F));   // version 7
    id[8] = (byte)(0x80 | (id[8] & 0x3F));   // RFC variant 10xx
  }

  private static Guid _withHash(Span<byte> id, string canonical) {
    Span<byte> hash = stackalloc byte[32];
    SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);
    id[11] |= (byte)(hash[0] & 0x0F);
    hash[1..5].CopyTo(id[12..]);
    return new Guid(id, bigEndian: true);
  }
}
