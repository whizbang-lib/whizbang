using System;
using System.Collections.Generic;
using Whizbang.Core.Attributes;

namespace Whizbang.Core;

/// <summary>
/// Runtime lookup of a message type's ephemeral mode. Indexes the compile-time
/// <see cref="IMessageTypeCatalog"/> (the authority, produced by the source generator from
/// <see cref="EphemeralAttribute"/>) into a fast ClrTypeName -&gt; <see cref="EphemeralInfo"/> map.
/// Ephemeral mode is NOT persisted in <c>wh_message_type_registry</c>, so the consumption-gated
/// reaper (<see cref="Destruction.WhenConsumed"/>) and the rebuild/rewind guards derive it from the
/// catalog at startup through this seam rather than a database round-trip.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public interface IEphemeralModeResolver {
  /// <summary>
  /// The ephemeral mode of the event type whose CLR name is <paramref name="clrTypeName"/> (the
  /// <c>Ns.Outer+Nested</c> form used across the registry), or <c>null</c> when the type is Sourced
  /// (the durable default) or is not present in the catalog.
  /// </summary>
  EphemeralInfo? Resolve(string clrTypeName);

  /// <summary>
  /// <c>true</c> when the type is declared ephemeral (carries an <see cref="EphemeralInfo"/> in the
  /// catalog); <c>false</c> for Sourced or unknown types.
  /// </summary>
  bool IsEphemeral(string clrTypeName);
}

/// <summary>
/// Default <see cref="IEphemeralModeResolver"/> — builds its lookup once from
/// <see cref="IMessageTypeCatalog.GetAll()"/> at construction (registered as a singleton), so a
/// per-lookup cost on the reaper/guard paths is a single ordinal dictionary probe.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed class EphemeralModeResolver : IEphemeralModeResolver {
  private readonly Dictionary<string, EphemeralInfo> _byClrTypeName;

  /// <summary>
  /// Indexes every catalog entry that carries an <see cref="MessageTypeCatalogEntry.Ephemeral"/>
  /// mode by its <see cref="MessageTypeCatalogEntry.ClrTypeName"/>. Sourced entries (null mode) are
  /// skipped, so a miss on <see cref="Resolve"/> unambiguously means "Sourced or unknown".
  /// </summary>
  /// <param name="catalog">The generated message-type catalog (the compile-time authority).</param>
  public EphemeralModeResolver(IMessageTypeCatalog catalog) {
    ArgumentNullException.ThrowIfNull(catalog);
    _byClrTypeName = new Dictionary<string, EphemeralInfo>(StringComparer.Ordinal);
    foreach (var entry in catalog.GetAll()) {
      if (entry.Ephemeral is not null) {
        _byClrTypeName[entry.ClrTypeName] = entry.Ephemeral;
      }
    }
  }

  /// <inheritdoc/>
  public EphemeralInfo? Resolve(string clrTypeName) =>
    _byClrTypeName.TryGetValue(clrTypeName, out var info) ? info : null;

  /// <inheritdoc/>
  public bool IsEphemeral(string clrTypeName) => _byClrTypeName.ContainsKey(clrTypeName);
}
