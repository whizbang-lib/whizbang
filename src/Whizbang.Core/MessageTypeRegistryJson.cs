using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Whizbang.Core;

/// <summary>
/// AOT-safe serialization of the message-type catalog into the JSONB payload consumed by the
/// <c>reconcile_message_type_registry</c> PL/pgSQL function. Shared by the Dapper and EFCore registry
/// populators and by the generated turnkey EFCore startup path, so the payload shape has a single definition.
/// </summary>
/// <remarks>
/// Manual string building (no <c>System.Text.Json</c> reflection) keeps this trim/AOT friendly. The array shape is
/// <c>[{"ClrTypeName":"…","PinnedId":"guid-or-null","Kind":"…","FormerNames":["old", …]}]</c>.
/// </remarks>
/// <docs>fundamentals/identity/pinned-type-ledger</docs>
/// <tests>tests/Whizbang.Core.Tests/MessageTypeRegistryJsonTests.cs</tests>
public static class MessageTypeRegistryJson {
  /// <summary>Serializes catalog entries to the reconcile JSONB array. Never returns null.</summary>
  public static string Serialize(IReadOnlyList<MessageTypeCatalogEntry> entries) {
    var sb = new StringBuilder("[");
    for (var i = 0; i < entries.Count; i++) {
      var e = entries[i];
      if (i > 0) {
        sb.Append(',');
      }
      sb.Append("{\"ClrTypeName\":").Append(JsonString(e.ClrTypeName))
        .Append(",\"PinnedId\":").Append(e.PinnedId is null ? "null" : JsonString(e.PinnedId))
        .Append(",\"Kind\":").Append(JsonString(e.Kind))
        .Append(",\"FormerNames\":").Append(JsonArray(e.FormerNames))
        .Append('}');
    }
    sb.Append(']');
    return sb.ToString();
  }

  /// <summary>Serializes a list of strings to a JSON array (empty/null -&gt; <c>[]</c>).</summary>
  public static string JsonArray(IReadOnlyList<string> values) {
    if (values is null || values.Count == 0) {
      return "[]";
    }
    var sb = new StringBuilder("[");
    for (var i = 0; i < values.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      sb.Append(JsonString(values[i]));
    }
    sb.Append(']');
    return sb.ToString();
  }

  /// <summary>Serializes a string to a JSON string literal (null -&gt; the literal <c>null</c>). Escapes control chars.</summary>
  public static string JsonString(string? value) {
    if (value is null) {
      return "null";
    }
    var sb = new StringBuilder("\"");
    foreach (var c in value) {
      switch (c) {
        case '"': sb.Append("\\\""); break;
        case '\\': sb.Append("\\\\"); break;
        case '\b': sb.Append("\\b"); break;
        case '\f': sb.Append("\\f"); break;
        case '\n': sb.Append("\\n"); break;
        case '\r': sb.Append("\\r"); break;
        case '\t': sb.Append("\\t"); break;
        default:
          if (c < 0x20) {
            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c);
          } else {
            sb.Append(c);
          }
          break;
      }
    }
    sb.Append('"');
    return sb.ToString();
  }
}
