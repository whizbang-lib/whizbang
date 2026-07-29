using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives a received message's persisted <see cref="EventFlags"/> for the transport receive path,
/// where the payload may be a <c>JsonElement</c> and runtime interface checks are blind. The catalog
/// (via <see cref="IEventMarkerResolver"/>, keyed by type name) is the authority; the typed checks
/// remain as the fallback for payloads the local catalog does not know.
/// </summary>
/// <docs>events/collective-events</docs>
public static class EventFlagsDeriver {
  /// <summary>
  /// The flags to persist for a received message. Name-first: when <paramref name="markerResolver"/>
  /// knows the type behind <paramref name="wireTypeName"/> (the assembly-qualified wire form; reduced
  /// here to the catalog's no-assembly <c>Ns.Outer+Nested</c> form), its compile-time stamp is
  /// returned — correct whether the payload is typed or a <c>JsonElement</c>. Otherwise falls back to
  /// the runtime checks, which only see typed payloads.
  /// </summary>
  public static EventFlags Derive(
      object? payload,
      string? wireTypeName,
      IEventMarkerResolver? markerResolver,
      IEphemeralModeResolver? ephemeralModeResolver) {
    if (markerResolver is not null && ToClrTypeName(wireTypeName) is { } clrTypeName) {
      var resolved = markerResolver.Resolve(clrTypeName);
      if (resolved is not null) {
        return resolved.Value;
      }
    }
    return (payload is ICompositeEvent ? EventFlags.Composite : EventFlags.None)
         | (payload is ICollectiveEvent ? EventFlags.Collective : EventFlags.None)
         | EphemeralFlagDeriver.Derive(payload, ephemeralModeResolver);
  }

  /// <summary>
  /// Reduces an assembly-qualified wire type name ("Ns.Outer+Nested, Asm, Version=…") to the
  /// catalog's no-assembly <c>ClrTypeName</c> form. Generic payload type names (containing
  /// <c>[[</c>) are not catalog-addressed — returns null so the caller uses the typed fallback.
  /// </summary>
  internal static string? ToClrTypeName(string? wireTypeName) {
    if (string.IsNullOrWhiteSpace(wireTypeName) ||
        wireTypeName.Contains("[[", StringComparison.Ordinal)) {
      return null;
    }
    var comma = wireTypeName.IndexOf(',');
    var name = (comma < 0 ? wireTypeName : wireTypeName[..comma]).Trim();
    return name.Length == 0 ? null : name;
  }
}
