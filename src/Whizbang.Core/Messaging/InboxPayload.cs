// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Text.Json;

namespace Whizbang.Core.Messaging;

/// <summary>How large an inbox payload is in memory, for the byte budgets on the inbox working set (#1793).</summary>
internal static class InboxPayload {
  /// <summary>
  /// The payload's UTF-8 JSON length, read from the parsed document without copying it. Zero for a payload that was
  /// never set.
  /// </summary>
  public static long Bytes(JsonElement payload) =>
    payload.ValueKind == JsonValueKind.Undefined ? 0 : JsonMarshal.GetRawUtf8Value(payload).Length;
}
