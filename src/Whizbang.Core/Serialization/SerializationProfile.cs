namespace Whizbang.Core.Serialization;

/// <summary>
/// Selects which JSON serialization profile <see cref="JsonContextRegistry.CreateCombinedOptions(SerializationProfile)"/>
/// builds. The same cross-assembly provider registry serves both, but WhizbangId structs need a different
/// byte format per path: the transport/event-store path serializes them as scalars (registered converters),
/// while perspective persistence needs object-mode (<c>{"Value":"…"}</c>) to match EF Core 10's jsonb mapping.
/// Providers can register for a specific profile; those registered without one apply to all profiles.
/// </summary>
public enum SerializationProfile {
  /// <summary>Transport / event-store profile — the historical default (scalar WhizbangId via converters).</summary>
  Default = 0,

  /// <summary>Perspective-persistence profile — object-mode WhizbangId for EF Core 10 jsonb round-trips.</summary>
  Persistence = 1
}
