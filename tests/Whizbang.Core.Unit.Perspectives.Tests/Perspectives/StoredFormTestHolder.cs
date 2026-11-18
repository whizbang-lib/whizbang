// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

// Shared by the unit and component projects (one source, compiled into both).

/// <summary>A holder whose instant is read by the canonical reader, as a document's would be.</summary>
public sealed class Holder {
  [System.Text.Json.Serialization.JsonConverter(typeof(CanonicalTemporalJsonConverters.InstantConverter))]
  public DateTime At { get; set; }
}

/// <summary>Source-generated metadata for <see cref="Holder"/>.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Holder))]
public sealed partial class HolderContext : System.Text.Json.Serialization.JsonSerializerContext;
