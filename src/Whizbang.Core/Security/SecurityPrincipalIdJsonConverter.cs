using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Core.Security;

/// <summary>
/// AOT-compatible JSON converter for SecurityPrincipalId.
/// Serializes SecurityPrincipalId as a simple string value.
/// </summary>
/// <remarks>
/// This converter ensures that AllowedPrincipals arrays in PerspectiveScope
/// are serialized as ["user:alice", "group:sales"] rather than
/// [{"Value": "user:alice"}, {"Value": "group:sales"}].
/// This is important for efficient JSONB array queries using PostgreSQL's
/// containment operators (@&gt;, ?|, etc.).
/// </remarks>
/// <docs>core-concepts/security#security-principals</docs>
/// <tests>Whizbang.Core.Tests/Security/SecurityPrincipalIdJsonConverterTests.cs</tests>
public sealed class SecurityPrincipalIdJsonConverter : JsonConverter<SecurityPrincipalId> {
  /// <inheritdoc/>
  public override SecurityPrincipalId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    var value = reader.GetString();
    return value != null ? new SecurityPrincipalId(value) : default;
  }

  /// <inheritdoc/>
  public override void Write(Utf8JsonWriter writer, SecurityPrincipalId value, JsonSerializerOptions options) {
    writer.WriteStringValue(value.Value);
  }
}
