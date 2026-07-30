using System;
using System.Collections.Generic;

namespace Whizbang.Core;

/// <summary>
/// The union of every loaded assembly's generated <see cref="IMessageTypeCatalog"/>. Each
/// Whizbang-compiled assembly generates a catalog of the types it DECLARES; a running host is the
/// union of its assemblies (host + contracts + libraries), so the catalog the container serves must
/// be the union too — a single assembly's catalog would leave the receive-path flag derivation, the
/// ephemeral resolver, the registry populators, the rename tool, and the fingerprint reconciler
/// blind to every type declared elsewhere. Entries are materialized once at construction;
/// duplicates (same CLR <see cref="MessageTypeCatalogEntry.Type"/>) keep the first occurrence.
/// </summary>
/// <docs>core-concepts/pinned-identity</docs>
public sealed class CompositeMessageTypeCatalog : IMessageTypeCatalog {
  private readonly IReadOnlyList<MessageTypeCatalogEntry> _entries;

  /// <summary>Unions the given catalogs' entries, deduplicated by CLR type (first wins).</summary>
  public CompositeMessageTypeCatalog(IEnumerable<IMessageTypeCatalog> catalogs) {
    ArgumentNullException.ThrowIfNull(catalogs);
    var seen = new HashSet<Type>();
    var entries = new List<MessageTypeCatalogEntry>();
    foreach (var catalog in catalogs) {
      foreach (var entry in catalog.GetAll()) {
        if (seen.Add(entry.Type)) {
          entries.Add(entry);
        }
      }
    }
    _entries = entries;
  }

  /// <inheritdoc />
  public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => _entries;
}
