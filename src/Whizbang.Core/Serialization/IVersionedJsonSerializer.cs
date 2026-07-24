using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Core.Serialization;

/// <summary>
/// A serialization implementation locked to a specific <see cref="Version"/> of the JSON
/// serialization logic. The framework recalls the correct implementation by version when reading a
/// stored blob (see <see cref="IVersionedJsonSerializerRegistry"/>), so a blob written by an older
/// implementation is read by that implementation instead of misparsing — and can then be
/// re-serialized with the current one to upgrade it.
/// </summary>
/// <remarks>
/// This non-generic base is what the registry keys on, and what <b>type-agnostic</b> serializers
/// implement directly — a single instance serves any model type via the supplied
/// <see cref="JsonTypeInfo"/> (AOT-safe; the caller provides the source-generated type info).
/// <b>Type-specific</b> serializers implement <see cref="IVersionedJsonSerializer{T}"/>, which
/// extends this base so they still register and recall uniformly by <see cref="Version"/>.
/// A version captures HOW the payload JSON is shaped (options/wrapping), not the model type.
/// </remarks>
/// <docs>event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/VersionedJsonSerializerRegistryTests.cs</tests>
public interface IVersionedJsonSerializer {
  /// <summary>The serialization-logic version this implementation reads and writes.</summary>
  int Version { get; }

  /// <summary>Serializes a model payload in this version's format using the supplied type info.</summary>
  JsonDocument SerializePayload(object model, JsonTypeInfo typeInfo);

  /// <summary>Deserializes a model payload that was written in this version's format.</summary>
  object DeserializePayload(JsonElement payload, JsonTypeInfo typeInfo);
}

/// <summary>
/// Type-safe versioned serializer for a specific model type <typeparamref name="T"/>. Extends the
/// non-generic <see cref="IVersionedJsonSerializer"/> so it registers and is recalled by
/// <see cref="IVersionedJsonSerializer.Version"/> like any other.
/// </summary>
/// <typeparam name="T">The model type this serializer handles.</typeparam>
/// <docs>event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/VersionedJsonSerializerRegistryTests.cs</tests>
public interface IVersionedJsonSerializer<T> : IVersionedJsonSerializer {
  /// <summary>Serializes a typed model payload in this version's format.</summary>
  JsonDocument SerializePayload(T model, JsonTypeInfo<T> typeInfo);

  /// <summary>Deserializes a typed model payload written in this version's format.</summary>
  T DeserializePayload(JsonElement payload, JsonTypeInfo<T> typeInfo);
}
