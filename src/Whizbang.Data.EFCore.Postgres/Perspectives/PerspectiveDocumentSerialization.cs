using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// The one set of serializer options every perspective document is written and read with.
/// </summary>
/// <remarks>
/// <para>
/// A perspective document is written by the atomic upsert, which serializes it with the persistence
/// profile and sends it as a parameter. A document stored as one value is read back by Entity
/// Framework, and the options Entity Framework would reach for on its own are the data source's,
/// which are the default profile: the outbox, inbox and event store metadata columns read through
/// them in the wire's form, and they cannot change. So a document column is bound to these options
/// explicitly, through <see cref="ConverterFor{T}"/>, and the writer and the reader are one set of
/// options rather than two that have to agree.
/// </para>
/// <para>
/// Built from the serialization registry alone: the cross-assembly union under the persistence
/// profile, which every assembly's generated contexts join from their own module initializers.
/// Nothing else can contribute to it or clear it, so which options a document is written with
/// never depends on what ran before. Reused until the registry changes, because a set of options carries the
/// serializer's metadata cache and rebuilding per call throws it away; rebuilt when the registry
/// changes, because an assembly loaded late registers its contexts late and a set built before
/// that cannot resolve what it brought.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveDocumentSerializationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OpaqueDocumentRoundTripTests.cs</tests>
public static class PerspectiveDocumentSerialization {
  private static readonly Lock _gate = new();
  private static long _builtForGeneration = -1;
  private static JsonSerializerOptions? _built;

  /// <summary>
  /// The options for every perspective document: the registry's union under the persistence profile.
  /// </summary>
  /// <remarks>Reused across calls until <see cref="JsonContextRegistry.Generation"/> moves.</remarks>
  public static JsonSerializerOptions Options {
    get {
      var generation = JsonContextRegistry.Generation;
      lock (_gate) {
        if (_built is null || _builtForGeneration != generation) {
          _built = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
          _builtForGeneration = generation;
        }
        return _built;
      }
    }
  }

  /// <summary>
  /// A converter that stores a document of this type as text under the persistence profile, for a
  /// jsonb column Entity Framework reads as one value.
  /// </summary>
  /// <typeparam name="T">The document type: a perspective model, its metadata or its scope.</typeparam>
  /// <returns>The converter, resolving the type's metadata through <see cref="Options"/> on each use.</returns>
  /// <remarks>
  /// Resolves on each use rather than capturing the options once, so a document written after a
  /// late registration is written with options that know about it.
  /// </remarks>
  public static ValueConverter<T, string> ConverterFor<T>() where T : class =>
    new(document => Serialize(document), stored => Deserialize<T>(stored));

  /// <summary>Serializes a document with <see cref="Options"/>.</summary>
  /// <typeparam name="T">The document type.</typeparam>
  /// <param name="document">The document.</param>
  /// <returns>The document as text.</returns>
  public static string Serialize<T>(T document) where T : class =>
    JsonSerializer.Serialize(document, _typeInfo<T>());

  /// <summary>Deserializes a document with <see cref="Options"/>.</summary>
  /// <typeparam name="T">The document type.</typeparam>
  /// <param name="stored">The document as text.</param>
  /// <returns>The document.</returns>
  public static T Deserialize<T>(string stored) where T : class =>
    JsonSerializer.Deserialize(stored, _typeInfo<T>())
      ?? throw new JsonException($"A stored {typeof(T).Name} document was null, which no document is");

  /// <summary>
  /// A converter for a promoted jsonb column: a <c>[PhysicalField]</c> holding an object, a list or a
  /// dictionary, stored as one value under the persistence profile.
  /// </summary>
  /// <typeparam name="T">The property's type, which may be a value type.</typeparam>
  /// <returns>The converter.</returns>
  /// <remarks>
  /// <para>
  /// The column has to be read with the options the atomic upsert writes it with, for the same reason
  /// the document does: left to the data source, a date inside it written as the canonical number
  /// would be read under the default profile, which expects a rendering.
  /// </para>
  /// <para>
  /// Unlike a document, a column can hold the JSON literal <c>null</c>: a property set to null and
  /// written by a path that keeps the value rather than sending SQL NULL. Entity Framework never hands
  /// a converter a SQL NULL, so the literal is the only null that arrives here, and it reads back as
  /// the property's default rather than failing the row.
  /// </para>
  /// </remarks>
  public static ValueConverter<T, string> ColumnConverterFor<T>() =>
    new(value => JsonSerializer.Serialize(value, _typeInfo<T>()), stored => DeserializeColumn<T>(stored));

  /// <summary>Reads a promoted jsonb column's stored text with <see cref="Options"/>.</summary>
  /// <typeparam name="T">The property's type.</typeparam>
  /// <param name="stored">The stored text.</param>
  /// <returns>The value, or the type's default for the JSON literal <c>null</c>.</returns>
  public static T DeserializeColumn<T>(string stored) =>
    JsonSerializer.Deserialize(stored, _typeInfo<T>())!;

  /// <summary>
  /// The name a member of <paramref name="type"/> is stored under in a document written with <see cref="Options"/>:
  /// its JSON name when the serializer's metadata says which property it is, otherwise its own name.
  /// </summary>
  /// <param name="type">The document type.</param>
  /// <param name="member">The member's CLR name.</param>
  /// <returns>The stored name.</returns>
  public static string StoredName(Type type, string member) {
    ArgumentNullException.ThrowIfNull(type);
    if (Options.TryGetTypeInfo(type, out var info)) {
      foreach (var property in info.Properties) {
        if (property.AttributeProvider is System.Reflection.MemberInfo declared
            && string.Equals(declared.Name, member, StringComparison.Ordinal)) {
          return property.Name;
        }
      }
    }

    return member;
  }

  private static JsonTypeInfo<T> _typeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
}
