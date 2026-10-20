// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Name-based (RFC 4122 version 5) ids: the same namespace and name always produce the same id.
/// </summary>
/// <remarks>
/// <para>
/// For ids that must be derivable without coordination, such as a stream that every writer has to
/// agree on. A minted id cannot serve there, because two writers minting independently would land on
/// two different streams.
/// </para>
/// <para>
/// Byte-for-byte the RFC 4122 algorithm: SHA-1 over the namespace's big-endian bytes followed by the
/// UTF-8 name, the first 16 bytes kept, version 5 and the RFC variant stamped. The same id is produced
/// in Postgres by <c>uuid_generate_v5(namespace, name)</c> from the <c>uuid-ossp</c> extension. No
/// reflection and no allocation beyond the input buffer, so it is safe under AOT and on hot paths.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#name-based-ids</docs>
/// <tests>tests/Whizbang.Core.Tests/ValueObjects/NameBasedGuidTests.cs</tests>
public static class NameBasedGuid {
  /// <summary>Derives the version 5 id for <paramref name="name"/> within <paramref name="namespaceId"/>.</summary>
  /// <param name="namespaceId">The namespace the name is unique within.</param>
  /// <param name="name">The name. An empty name is a valid name and has its own id.</param>
  /// <returns>The RFC 4122 version 5 id.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
  public static Guid V5(Guid namespaceId, string name) {
    return Guid.Empty; // RED stub (#1485)
  }
}
