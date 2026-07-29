using System;
using System.Collections.Generic;
using Whizbang.Core.Messaging;

namespace Whizbang.Core;

/// <summary>
/// Runtime lookup of a message type's persisted <see cref="EventFlags"/> as stamped on the
/// compile-time <see cref="IMessageTypeCatalog"/> (Collective / Composite / Compacted markers plus the
/// resolved Ephemeral mode). Load-bearing on the transport receive path, where the payload is a
/// <c>JsonElement</c> and runtime interface checks (<c>payload is ICollectiveEvent</c>) are blind — a
/// received event's flags must be derived from the catalog by type name or the bits that drive sink
/// routing, the reaper, and the replay guards are silently lost at every service boundary.
/// </summary>
/// <docs>events/collective-events</docs>
public interface IEventMarkerResolver {
  /// <summary>
  /// The catalog-stamped flags of the type whose CLR name is <paramref name="clrTypeName"/> (the
  /// <c>Ns.Outer+Nested</c> form), or <c>null</c> when the type is not in the catalog — a miss means
  /// "unknown here", so callers fall back to runtime type checks rather than assuming
  /// <see cref="EventFlags.None"/>.
  /// </summary>
  EventFlags? Resolve(string clrTypeName);

  /// <summary>
  /// The catalog-stamped flags of the runtime <paramref name="type"/> (the typed dispatch path), or
  /// <c>null</c> when it is not in the catalog.
  /// </summary>
  EventFlags? Resolve(Type type);
}

/// <summary>
/// Default <see cref="IEventMarkerResolver"/> — indexes <see cref="IMessageTypeCatalog.GetAll()"/> once
/// at construction (registered as a singleton), so a per-message lookup on the receive path is a single
/// ordinal dictionary probe.
/// </summary>
/// <docs>events/collective-events</docs>
public sealed class EventMarkerResolver : IEventMarkerResolver {
  private readonly Dictionary<string, EventFlags> _byClrTypeName;
  private readonly Dictionary<Type, EventFlags> _byType;

  /// <summary>
  /// Indexes every catalog entry by both its <see cref="MessageTypeCatalogEntry.ClrTypeName"/> (the
  /// wire/transport lookup) and its <see cref="MessageTypeCatalogEntry.Type"/> (the typed dispatch
  /// path). Every entry is indexed — a plain Sourced event resolves to <see cref="EventFlags.None"/>,
  /// so only a genuinely unknown type yields a null lookup.
  /// </summary>
  /// <param name="catalog">The generated message-type catalog (the compile-time authority).</param>
  public EventMarkerResolver(IMessageTypeCatalog catalog) {
    ArgumentNullException.ThrowIfNull(catalog);
    var entries = catalog.GetAll();
    _byClrTypeName = new Dictionary<string, EventFlags>(entries.Count, StringComparer.Ordinal);
    _byType = new Dictionary<Type, EventFlags>(entries.Count);
    foreach (var entry in entries) {
      var flags = (entry.IsCollective ? EventFlags.Collective : EventFlags.None)
                | (entry.IsComposite ? EventFlags.Composite : EventFlags.None)
                // Compacted wins over Ephemeral (a compaction summary is permanent StateBased, never
                // reaped) — the same precedence EphemeralFlagDeriver applies on the typed path.
                | (entry.IsCompacted ? EventFlags.Compacted
                   : entry.Ephemeral is not null ? EventFlags.Ephemeral : EventFlags.None);
      _byClrTypeName[entry.ClrTypeName] = flags;
      _byType[entry.Type] = flags;
    }
  }

  /// <summary>
  /// Number of distinct CLR type names indexed from the catalog — a startup diagnostic: a host-only
  /// count here (instead of the union of all loaded assemblies) is the fingerprint of a broken
  /// cross-assembly catalog union.
  /// </summary>
  public int IndexedTypeNameCount => _byClrTypeName.Count;

  /// <inheritdoc />
  public EventFlags? Resolve(string clrTypeName)
    => clrTypeName is not null && _byClrTypeName.TryGetValue(clrTypeName, out var flags) ? flags : null;

  /// <inheritdoc />
  public EventFlags? Resolve(Type type)
    => type is not null && _byType.TryGetValue(type, out var flags) ? flags : null;
}
