// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;

namespace Whizbang.Core;

/// <summary>
/// The RFC 9562 UUID versions a strongly-typed id accepts, as flags (<see cref="WhizbangIdAttribute.Versions"/>).
/// </summary>
/// <remarks>
/// Each flag's bit is its version number (<c>V7 == 1 &lt;&lt; 7</c>), so a Guid's <see cref="Guid.Version"/> tests its
/// flag directly. The nil and max UUIDs are not versions and have no flag.
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids</docs>
/// <tests>tests/Whizbang.Core.Tests/WhizbangIdAttributeTests.cs:UuidVersions_EachFlagIsTheBitOfItsVersionNumberAsync</tests>
[Flags]
public enum UuidVersions {
  /// <summary>No version. An id that accepts none is an error (WHIZ039).</summary>
  None = 0,

  /// <summary>Version 1: time and node based (Gregorian timestamp, MAC address).</summary>
  V1 = 1 << 1,

  /// <summary>Version 2: DCE security.</summary>
  V2 = 1 << 2,

  /// <summary>Version 3: name based, MD5.</summary>
  V3 = 1 << 3,

  /// <summary>Version 4: random, for example <c>crypto.randomUUID()</c> or <see cref="Guid.NewGuid"/>.</summary>
  V4 = 1 << 4,

  /// <summary>Version 5: name based, SHA-1, for example <c>NameBasedGuid.V5</c>.</summary>
  V5 = 1 << 5,

  /// <summary>Version 6: reordered Gregorian timestamp.</summary>
  V6 = 1 << 6,

  /// <summary>Version 7: Unix-epoch timestamp, time-ordered. What <c>New()</c> mints, and the default.</summary>
  V7 = 1 << 7,

  /// <summary>Version 8: custom, application-defined layout.</summary>
  V8 = 1 << 8,
}
