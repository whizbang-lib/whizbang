// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Core.Serialization;

/// <summary>
/// The converters the generated message JSON context attaches to every vector property (a <c>float[]</c> or an
/// <c>IReadOnlyList&lt;float&gt;</c> not marked <see cref="Attributes.StoredAsJsonArrayAttribute"/>). Each reads
/// either stored form of the vector and always writes the JSON array (#2624).
/// </summary>
/// <remarks>
/// <para>
/// The two forms are told apart by token type. A JSON array of numbers is the wire form and the form every body was
/// stored in before packing; it is read exactly as the property's own metadata reads it. A JSON string is the packed
/// form: base64, without line breaks, of the big-endian IEEE-754 binary32 bits of each element in order. It is
/// lossless by construction, NaN payloads and <c>-0</c> included.
/// </para>
/// <para>
/// Writing delegates to the property's own array metadata, so the written bytes are exactly what they were before the
/// converter was attached. Readers ship before any writer packs: the stored-body packing (V2) is gated so that no body
/// is packed while an instance that cannot read the packed form is live.
/// </para>
/// </remarks>
/// <docs>data/event-store#packed-vectors</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorConvertersTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorEventReadTests.cs</tests>
public static class StoredVectorConverters {
  /// <summary>The converter for a <c>float[]</c> vector property.</summary>
  /// <param name="propertyName">The property's JSON name, named by a read that fails.</param>
  /// <param name="arrayForm">The property's own metadata, which reads and writes the array form.</param>
  /// <returns>A converter that reads either form and writes the array.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorConvertersTests.cs:ThePackedForm_ReadsEdgeValues_BitExactAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorConvertersTests.cs:Writing_AlwaysWritesTheArrayForm_AsTheDefaultWriterDoesAsync</tests>
  public static JsonConverter<float[]> ForArray(string propertyName, JsonTypeInfo<float[]> arrayForm) =>
    new StoredVectorJsonConverter<float[]>(propertyName, arrayForm, static packed => packed);

  /// <summary>The converter for an <c>IReadOnlyList&lt;float&gt;</c> vector property.</summary>
  /// <param name="propertyName">The property's JSON name, named by a read that fails.</param>
  /// <param name="arrayForm">The property's own metadata, which reads and writes the array form.</param>
  /// <returns>A converter that reads either form and writes the array.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorConvertersTests.cs:AReadOnlyListVector_ReadsEitherForm_AndWritesTheArrayAsync</tests>
  public static JsonConverter<IReadOnlyList<float>> ForReadOnlyList(string propertyName, JsonTypeInfo<IReadOnlyList<float>> arrayForm) =>
    new StoredVectorJsonConverter<IReadOnlyList<float>>(propertyName, arrayForm, static packed => packed);
}

/// <summary>Reads a vector stored as a JSON array or packed binary32, and writes the array.</summary>
/// <typeparam name="TVector">The property's vector type.</typeparam>
internal sealed class StoredVectorJsonConverter<TVector> : JsonConverter<TVector> {
  private readonly string _propertyName;
  private readonly JsonTypeInfo<TVector> _arrayForm;
  private readonly Func<float[], TVector> _fromPacked;

  internal StoredVectorJsonConverter(string propertyName, JsonTypeInfo<TVector> arrayForm, Func<float[], TVector> fromPacked) {
    ArgumentNullException.ThrowIfNull(propertyName);
    ArgumentNullException.ThrowIfNull(arrayForm);
    _propertyName = propertyName;
    _arrayForm = arrayForm;
    _fromPacked = fromPacked;
  }

  /// <inheritdoc/>
  public override TVector? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
    reader.TokenType == JsonTokenType.String
      ? _fromPacked(_unpack(ref reader))
      : JsonSerializer.Deserialize(ref reader, _arrayForm);

  /// <inheritdoc/>
  public override void Write(Utf8JsonWriter writer, TVector value, JsonSerializerOptions options) =>
    JsonSerializer.Serialize(writer, value, _arrayForm);

  private float[] _unpack(ref Utf8JsonReader reader) {
    if (!reader.TryGetBytesFromBase64(out var bytes) || bytes.Length % sizeof(float) != 0) {
      throw new JsonException(
        $"The stored vector '{_propertyName}' is a string that is not base64 of whole binary32 values, so it cannot be read as packed floats.");
    }
    var values = new float[bytes.Length / sizeof(float)];
    for (var i = 0; i < values.Length; i++) {
      values[i] = BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(i * sizeof(float)));
    }
    return values;
  }
}
