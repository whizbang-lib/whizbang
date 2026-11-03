// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.ComponentModel;

namespace Whizbang.Core.Archival;

/// <summary>
/// The end-of-life policies declared with <see cref="Attributes.StreamEndOfLifeAttribute"/>, registered by every assembly's
/// generated message catalog when the assembly loads (#1740). <see cref="StreamLifecycleOptions"/> starts from them, and
/// configuration wins key by key.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#declaring-a-policy-on-the-event-type</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamEndOfLifeDeclarationsTests.cs</tests>
public static class StreamEndOfLifeDeclarations {
  private static readonly ConcurrentDictionary<string, StreamEndOfLifeDeclaration> _declarations = new(StringComparer.Ordinal);

  /// <summary>Registers a declaration. Called by generated code; registering a stream type again keeps the first.</summary>
  /// <param name="declaration">The declaration.</param>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public static void Register(StreamEndOfLifeDeclaration declaration) {
    ArgumentNullException.ThrowIfNull(declaration);
    _declarations.TryAdd(declaration.StreamType, declaration);
  }

  /// <summary>Every registered declaration, ordered by stream type.</summary>
  /// <returns>The declarations.</returns>
  public static IReadOnlyList<StreamEndOfLifeDeclaration> All() =>
    [.. _declarations.Values.OrderBy(d => d.StreamType, StringComparer.Ordinal)];
}

/// <summary>One event type's <see cref="Attributes.StreamEndOfLifeAttribute"/>, as the generator registers it.</summary>
/// <param name="StreamType">The stream type as the lifecycle ledger records it (the event type's CLR name without its assembly).</param>
/// <param name="IdleTtl">The idle trigger as written, or null.</param>
/// <param name="MaxAge">The age trigger as written, or null.</param>
/// <param name="KeepTailVersions">The version cap; 0 for none.</param>
/// <param name="TenantByteCap">The per-tenant byte cap; 0 for none.</param>
/// <param name="Action">The action.</param>
/// <docs>fundamentals/events/stream-end-of-life#declaring-a-policy-on-the-event-type</docs>
public sealed record StreamEndOfLifeDeclaration(
  string StreamType, string? IdleTtl, string? MaxAge, long KeepTailVersions, long TenantByteCap, StreamLifecycleAction Action);
