// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Serialization;

/// <summary>
/// Selects which JSON serialization profile <see cref="JsonContextRegistry.CreateCombinedOptions(SerializationProfile)"/>
/// builds. The same cross-assembly provider registry serves both, but WhizbangId structs read differently per path:
/// both write the UUID string, while the transport/event-store path reads through the id's <c>From</c> (its version
/// rule) and perspective persistence reads a stored row unchecked, in the UUID string or the object
/// <c>{"Value":"…"}</c> a release before #2518 stored.
/// Providers can register for a specific profile; those registered without one apply to all profiles.
/// </summary>
public enum SerializationProfile {
  /// <summary>Transport / event-store profile: the historical default (scalar WhizbangId via converters).</summary>
  Default = 0,

  /// <summary>Perspective-persistence profile: WhizbangId as stored in a document, read unchecked in either form.</summary>
  Persistence = 1
}
