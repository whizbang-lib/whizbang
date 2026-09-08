using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives the identity of an event a handler emits while handling an inbound message, so that a
/// retry of that handling re-derives the same ids instead of minting new ones.
/// </summary>
/// <remarks>
/// <para>
/// A retry is not a republish. When an inbox row is dispatched a second time (its first run's
/// completion never committed, or its lease lapsed and it was re-offered), the handler runs again and
/// emits again. With fresh ids per run every copy is a legitimately new message: it is appended to the
/// event store, placed in the outbox, published, and fanned out to every subscriber. With ids derived
/// from the handling itself the second run produces the same ids, and the store's primary keys turn the
/// duplicate into a counted no-op.
/// </para>
/// <para>
/// <strong>Layout (UUIDv7-shaped, derived entropy):</strong> bytes 0..5 are the first 48 bits of the
/// source message id (its UUIDv7 millisecond timestamp when the source is a framework id), so derived ids
/// stay time-local to their source and keep index locality; byte 6 carries the version nibble 7 over the
/// top four bits of the hash; byte 8 carries the RFC variant over the top two bits of the hash; every
/// remaining bit (74 in all) is SHA-256 over the UTF-8 canonical string
/// <c>whizbang.emission.v1\n{source}\n{service}\n{handler}\n{emittedType}\n{ordinal}</c>. RFC 9562 lets
/// a v7 id fill its non-timestamp bits with any implementation-chosen data; deriving them from the handling
/// is what makes the id repeatable. The version stays 7 so every consumer that requires time-ordered ids
/// (the framework's id value objects among them) accepts a derived id exactly like a minted one.
/// </para>
/// <para>
/// The five inputs are the ones that make an emission unique within a fleet: the source message
/// (which handling), the producing service (two services handling the same message must not collide on
/// the wire), the handler within the service (two handler rows of one message must not collide), the
/// emitted type, and the ordinal of the emission within the handling (the same type emitted twice).
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EmissionIdentityTests.cs</tests>
public static class EmissionIdentity {
  private const string PREFIX = "whizbang.emission.v1";

  /// <summary>
  /// Derives a deterministic event id for the <paramref name="ordinal"/>th emission of
  /// <paramref name="emittedTypeName"/> while <paramref name="serviceName"/> handles
  /// <paramref name="sourceMessageId"/> in <paramref name="handlerName"/>.
  /// </summary>
  /// <param name="sourceMessageId">The inbound message being handled. Must not be empty.</param>
  /// <param name="serviceName">The producing service's name.</param>
  /// <param name="handlerName">The handler (inbox row) within the service, or null when the handling has no per-handler identity.</param>
  /// <param name="emittedTypeName">The emitted message type, rendered by <see cref="TypeNameFormatter"/>.</param>
  /// <param name="ordinal">Zero-based position of this emission within the handling.</param>
  /// <returns>A UUIDv7-shaped id that is identical for identical inputs.</returns>
  /// <exception cref="ArgumentException">The source message id is empty.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The ordinal is negative.</exception>
  public static Guid Derive(Guid sourceMessageId, string serviceName, string? handlerName, string emittedTypeName, int ordinal) {
    if (sourceMessageId == Guid.Empty) {
      throw new ArgumentException("A deterministic emission id needs the source message it was emitted from.", nameof(sourceMessageId));
    }
    ArgumentNullException.ThrowIfNull(serviceName);
    ArgumentNullException.ThrowIfNull(emittedTypeName);
    ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

    var canonical = string.Concat(
      PREFIX, "\n",
      sourceMessageId.ToString("N"), "\n",
      serviceName, "\n",
      handlerName ?? string.Empty, "\n",
      emittedTypeName, "\n",
      ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));

    Span<byte> hash = stackalloc byte[32];
    SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);

    Span<byte> source = stackalloc byte[16];
    sourceMessageId.TryWriteBytes(source, bigEndian: true, out _);

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

/// <summary>
/// Hands out the ordinal of each emission within one handling. Keyed by the handling's identity object
/// (the source envelope instance a receptor invoker threads through, or the initiating message context),
/// so a retry, which materializes a fresh envelope, starts again at zero and re-derives the same sequence.
/// </summary>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EmissionIdentityTests.cs</tests>
public static class EmissionSequence {
  private static readonly ConditionalWeakTable<object, Counter> _counters = [];

  /// <summary>Returns the next zero-based ordinal for <paramref name="handlingKey"/>.</summary>
  public static int Next(object handlingKey) {
    ArgumentNullException.ThrowIfNull(handlingKey);
    var counter = _counters.GetValue(handlingKey, static _ => new Counter());
    return Interlocked.Increment(ref counter.Value) - 1;
  }

  private sealed class Counter {
    public int Value;
  }
}
