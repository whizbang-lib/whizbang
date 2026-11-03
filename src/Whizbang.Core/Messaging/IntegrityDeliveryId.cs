// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The identity of an integrity delivery, derived from its type and payload bytes (#2001), so the inbox, outbox and
/// local copies of one message, and a delivery retried after a failed commit, are recorded once.
/// </summary>
/// <remarks>
/// SHA-256 over the type's length, the type and the payload, truncated to 128 bits and stamped as an RFC 9562 version 8
/// (custom) identifier. The type is length-prefixed so that no type and payload pair can collide with another by moving
/// bytes across the boundary.
/// </remarks>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDeliveryDeferralTests.cs:TheDeliveryId_IsAFunctionOfTypeAndPayloadAsync</tests>
public static class IntegrityDeliveryId {
  /// <summary>The identity of the delivery of <paramref name="payload"/> as <paramref name="deliveryType"/>.</summary>
  /// <param name="deliveryType">The delivery type.</param>
  /// <param name="payload">The serialized delivery.</param>
  /// <returns>A deterministic version 8 identifier.</returns>
  public static Guid For(string deliveryType, ReadOnlySpan<byte> payload) {
    ArgumentNullException.ThrowIfNull(deliveryType);
    var type = Encoding.UTF8.GetBytes(deliveryType);
    var input = new byte[sizeof(int) + type.Length + payload.Length];
    BinaryPrimitives.WriteInt32BigEndian(input, type.Length);
    type.CopyTo(input, sizeof(int));
    payload.CopyTo(input.AsSpan(sizeof(int) + type.Length));
    Span<byte> hash = stackalloc byte[32];
    SHA256.HashData(input, hash);
    var bytes = hash[..16];
    bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);   // version 8
    bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 9562 variant
    return new Guid(bytes, bigEndian: true);
  }
}
