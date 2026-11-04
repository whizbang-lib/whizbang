// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Resolves an event's stored or wire type name to its type for the audit decision, through the generated message-type
/// catalog. The live outbox path (<see cref="AuditOutboxMessageBuilder"/>) and the ledger rebuild
/// (<see cref="AuditLedgerRebuild"/>) both resolve through it, so a type is resolvable on both paths or on neither.
/// </summary>
/// <remarks>
/// <para>
/// The live path used to resolve with <c>Type.GetType</c> and the rebuild through the catalog (#2040). A loaded type
/// outside the catalog then resolved live and not on rebuild, and a catalog type written by its full name alone resolved
/// on rebuild and not live, so the two disagreed about whether the event was audited: the gap #1980 closed for types
/// neither path resolves. A type this resolver does not find is decided by the audit mode alone, on both paths.
/// </para>
/// <para>
/// Built once from <see cref="IMessageTypeCatalog.GetAll()"/> and registered as a singleton, so the per-event cost on
/// the outbox path is one or two ordinal dictionary probes, and no reflection is involved.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/audit-logging#one-type-resolution</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditLedgerRebuildTests.cs:LiveAndRebuild_AgreeWhereTypeGetTypeAndTheCatalogDisagree_UnderEitherModeAsync</tests>
public sealed class AuditTypeResolver {
  private readonly Dictionary<string, Type> _types;

  /// <summary>Indexes every type in <paramref name="catalog"/> by each name form an event is stored or sent under.</summary>
  /// <param name="catalog">The generated message-type catalog.</param>
  public AuditTypeResolver(IMessageTypeCatalog catalog) {
    ArgumentNullException.ThrowIfNull(catalog);
    _types = EventTypeMatchingHelper.BuildTypeLookup([.. catalog.GetAll().Select(e => e.Type)]);
  }

  /// <summary>Resolves <paramref name="typeName"/>, in its stored or its assembly-qualified form.</summary>
  /// <param name="typeName">The event's type name, as the outbox row or the event store carries it.</param>
  /// <param name="type">The type, when it is in the catalog.</param>
  /// <returns><see langword="true"/> when the catalog holds the type.</returns>
  public bool TryResolve(string typeName, [NotNullWhen(true)] out Type? type) =>
    EventTypeMatchingHelper.TryResolveType(_types, typeName, out type);
}
