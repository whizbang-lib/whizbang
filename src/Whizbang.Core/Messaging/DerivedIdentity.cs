using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The one layout for ids the framework derives instead of mints: UUIDv7-shaped, the 48-bit time prefix
/// inherited from a source id so a derived id stays time-local to what it was derived from, and every
/// other bit taken from SHA-256 over a canonical string that names the derivation. RFC 9562 lets a v7 id
/// fill its non-timestamp bits with implementation-chosen data; deriving them from the inputs is what
/// makes the id repeatable, and keeping the version at 7 is what lets every consumer that requires
/// time-ordered ids accept a derived id exactly like a minted one.
/// </summary>
/// <remarks>
/// Used by <see cref="EmissionIdentity"/> (a handler's emissions) and <see cref="CompositeChildIdentity"/>
/// (a composite's children). Each caller owns its canonical prefix, so the two derivations can never
/// collide even for identical remaining inputs.
/// </remarks>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EmissionIdentityTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeChildIdentityTests.cs</tests>
internal static class DerivedIdentity {
  /// <summary>Derives the id whose time prefix comes from <paramref name="timeSource"/> and whose remaining bits hash <paramref name="canonical"/>.</summary>
  internal static Guid FromCanonical(Guid timeSource, string canonical) {
    Span<byte> hash = stackalloc byte[32];
    SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);

    Span<byte> source = stackalloc byte[16];
    timeSource.TryWriteBytes(source, bigEndian: true, out _);

    Span<byte> id = stackalloc byte[16];
    // Time prefix inherited from the source (bytes 0..5), then hash with version and variant bits.
    source[..6].CopyTo(id);
    id[6] = (byte)(0x70 | (hash[0] & 0x0F));   // version 7
    id[7] = hash[1];
    id[8] = (byte)(0x80 | (hash[2] & 0x3F));   // RFC variant 10xx
    hash[3..10].CopyTo(id[9..]);
    return new Guid(id, bigEndian: true);
  }
}
