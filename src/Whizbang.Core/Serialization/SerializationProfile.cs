// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Serialization;

/// <summary>
/// Selects which JSON serialization profile <see cref="JsonContextRegistry.CreateCombinedOptions(SerializationProfile)"/>
/// builds. The same cross-assembly provider registry serves both, but WhizbangId structs read differently per path:
/// the transport/event-store path writes the UUID string and reads through the id's <c>From</c> (its version rule);
/// perspective persistence writes the form the whole fleet reads (the object <c>{"Value":"…"}</c> until every live
/// instance reads the UUID string, <see cref="Whizbang.Core.Perspectives.WhizbangIdDocumentForm"/>) and reads a stored
/// row unchecked, in either form.
/// Providers can register for a specific profile; those registered without one apply to all profiles.
/// </summary>
public enum SerializationProfile {
  /// <summary>Transport / event-store profile: the historical default (scalar WhizbangId via converters).</summary>
  Default = 0,

  /// <summary>Perspective-persistence profile: WhizbangId as stored in a document, read unchecked in either form.</summary>
  Persistence = 1
}
