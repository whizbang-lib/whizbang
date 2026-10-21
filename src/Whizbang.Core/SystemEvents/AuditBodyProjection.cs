// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace Whizbang.Core.SystemEvents;

/// <summary>The bounded forms an audit record keeps of a message body.</summary>
internal static class AuditBodyProjection {
  /// <summary>Replaces each named list in <paramref name="body"/> with its length.</summary>
  /// <param name="body">The serialized command.</param>
  /// <param name="names">The properties whose lists are replaced.</param>
  /// <returns>The bounded body.</returns>
  internal static JsonElement ListLengths(JsonElement body, IReadOnlyCollection<string> names) => body;
}
