// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The stream a collective event with an <see cref="ICollectiveEvent.OrderingKey"/> is stored on. Collectives that
/// share a key in one scope share this stream, so they share one <c>__collective__</c> sink stream, and the perspective
/// worker applies one sink stream at a time and in commit order.
/// </summary>
/// <remarks>
/// <para>
/// The id is derived from the scope's <see cref="CollectiveScope.ScopeIdentity"/> and the key, not minted, so every
/// producer, every consumer and every replay computes the same stream without coordinating. The scope is part of it
/// because two tenants' collectives touch disjoint rows: ordering them against each other would only serialize them.
/// The event type is not, because the collectives a key orders are usually different types, the two halves of a swap.
/// </para>
/// <para>
/// <strong>This is a compatibility contract.</strong> Once keyed collectives are stored, a change to the derivation
/// splits each key into two streams across a deploy, and the two halves are no longer ordered against each other.
/// The value is pinned by an exact assertion for that reason.
/// </para>
/// <para>
/// The id is the first sixteen bytes of a SHA-256 over the scope identity and the key, marked as a version 8 UUID
/// (RFC 9562's custom form) so it never reads as a minted, time-ordered UUIDv7.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveOrderingKeyTests.cs</tests>
public static class CollectiveOrdering {
  // Namespaces the hash so no other derived id in the framework can collide with a collective's.
  private const string DOMAIN = "whizbang:collective-ordering\n";

  /// <summary>The stream collectives sharing <paramref name="orderingKey"/> in <paramref name="scope"/> are stored on.</summary>
  /// <param name="scope">The collective's scope; its <see cref="CollectiveScope.ScopeIdentity"/> is part of the id.</param>
  /// <param name="orderingKey">The ordering key the producer chose.</param>
  /// <returns>A version 8 UUID, the same for every caller with the same scope identity and key.</returns>
  public static Guid StreamIdFor(CollectiveScope scope, string orderingKey) {
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentException.ThrowIfNullOrWhiteSpace(orderingKey);

    Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
    SHA256.HashData(Encoding.UTF8.GetBytes(DOMAIN + scope.ScopeIdentity + "\n" + orderingKey), hash);
    var id = hash[..16];
    id[6] = (byte)((id[6] & 0x0F) | 0x80);  // version 8
    id[8] = (byte)((id[8] & 0x3F) | 0x80);  // RFC 9562 variant
    return new Guid(id, bigEndian: true);
  }
}
