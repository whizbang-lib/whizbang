// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Issue #2624 (V1 of the vector storage plan): a vector property of a stored event reads either form, a JSON array of
/// numbers or one string holding base64 of the big-endian IEEE-754 binary32 bits of each element, and always writes
/// the array. The packed form is bit-exact by construction, so every value a <see cref="float"/> can hold reads back
/// with the same bits, including those a JSON number cannot carry.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Serialization/StoredVectorConverters.cs</code-under-test>
[Category("Serialization")]
public class StoredVectorConvertersTests {
  private static readonly JsonSerializerOptions _options = JsonContextRegistry.CreateCombinedOptions();

  private static JsonTypeInfo<T> _info<T>() => (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T));

  /// <summary>The packed form of <paramref name="bits"/>, written independently of the converter under test.</summary>
  internal static string PackBits(params int[] bits) {
    var bytes = new byte[bits.Length * 4];
    for (var i = 0; i < bits.Length; i++) {
      BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(i * 4), bits[i]);
    }
    return Convert.ToBase64String(bytes);
  }

  /// <summary>The packed form of <paramref name="values"/>.</summary>
  internal static string Pack(params float[] values) => PackBits([.. values.Select(BitConverter.SingleToInt32Bits)]);

  private static T? _read<T>(JsonConverter<T> converter, string json) {
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read();
    return converter.Read(ref reader, typeof(T), _options);
  }

  private static string _write<T>(JsonConverter<T> converter, T value) {
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream)) {
      converter.Write(writer, value, _options);
    }
    return Encoding.UTF8.GetString(stream.ToArray());
  }

  private static int[] _bits(IEnumerable<float> values) => [.. values.Select(BitConverter.SingleToInt32Bits)];

  private static JsonConverter<float[]> _arrayConverter() => StoredVectorConverters.ForArray("Embedding", _info<float[]>());

  [Test]
  public async Task ThePackedForm_IsBigEndianBinary32_InElementOrderAsync() {
    // 1.0f is 0x3F800000 and -2.0f is 0xC0000000: big-endian bytes 3F 80 00 00 C0 00 00 00.
    var read = _read(_arrayConverter(), "\"P4AAAMAAAAA=\"")!;

    float[] expected = [1.0f, -2.0f];
    await Assert.That(read).IsEquivalentTo(expected)
      .Because("the byte order and element order are the stored contract the SQL packing (V2) must match");
  }

  [Test]
  public async Task ThePackedForm_ReadsEdgeValues_BitExactAsync() {
    int[] bits = [
      BitConverter.SingleToInt32Bits(float.NaN),
      0x7FC00001, // a quiet NaN with a payload
      unchecked((int)0xFFC00000), // a negative NaN
      0x7F800001, // a signaling NaN
      BitConverter.SingleToInt32Bits(float.PositiveInfinity),
      BitConverter.SingleToInt32Bits(float.NegativeInfinity),
      BitConverter.SingleToInt32Bits(-0.0f),
      0,
      BitConverter.SingleToInt32Bits(float.Epsilon), // the smallest subnormal
      0x007FFFFF, // the largest subnormal
      unchecked((int)0x80000001), // the smallest negative subnormal
      0x00800000, // the smallest normal
      BitConverter.SingleToInt32Bits(float.MaxValue),
      BitConverter.SingleToInt32Bits(float.MinValue),
    ];

    var read = _read(_arrayConverter(), $"\"{PackBits(bits)}\"")!;

    await Assert.That(_bits(read)).IsEquivalentTo(bits)
      .Because("the packed form carries the bits themselves, so no value, NaN payloads and -0 included, can change");
  }

  [Test]
  public async Task ThePackedForm_ReadsRandomVectors_BitExactAsync() {
    var random = new Random(2624);
    for (var round = 0; round < 8; round++) {
      var bits = new int[3072];
      for (var i = 0; i < bits.Length; i++) {
        bits[i] = random.Next(int.MinValue, int.MaxValue);
      }

      var read = _read(_arrayConverter(), $"\"{PackBits(bits)}\"")!;

      await Assert.That(_bits(read)).IsEquivalentTo(bits);
    }
  }

  [Test]
  public async Task BothForms_ReadToEqualArraysAsync() {
    float[] vector = [0.1f, -0.333333343f, 1e-38f, 3.4028235e38f, 1.17549435e-38f, 0f, 7f];
    var asArray = JsonSerializer.Serialize(vector, _info<float[]>());

    var fromArray = _read(_arrayConverter(), asArray)!;
    var fromPacked = _read(_arrayConverter(), $"\"{Pack(vector)}\"")!;

    await Assert.That(_bits(fromArray)).IsEquivalentTo(_bits(vector))
      .Because("the C# writer emits the shortest round-trip text for each float, so the array form is lossless too");
    await Assert.That(_bits(fromPacked)).IsEquivalentTo(_bits(fromArray));
  }

  [Test]
  public async Task AnEscapedPackedString_ReadsTheSameBitsAsync() {
    // base64 of FB FF FF FF is "+////w=="; a writer with the default encoder escapes '+'.
    var read = _read(_arrayConverter(), "\"\\u002B////w==\"")!;

    int[] expectedBits = [unchecked((int)0xFBFFFFFF)];
    await Assert.That(_bits(read)).IsEquivalentTo(expectedBits);
  }

  [Test]
  public async Task AnEmptyPackedString_ReadsAnEmptyVectorAsync() {
    var read = _read(_arrayConverter(), "\"\"")!;

    await Assert.That(read).IsEmpty();
  }

  [Test]
  public async Task AStringThatIsNotBase64_FailsNamingThePropertyAsync() {
    var failure = await Assert.That(() => _read(_arrayConverter(), "\"not base64!\"")).Throws<JsonException>();

    await Assert.That(failure!.Message).Contains("Embedding");
  }

  [Test]
  public async Task Base64OfBytesThatAreNotWholeBinary32Values_FailsNamingThePropertyAsync() {
    // base64 of three bytes: not a multiple of four.
    var failure = await Assert.That(() => _read(_arrayConverter(), "\"AAAA\"")).Throws<JsonException>();

    await Assert.That(failure!.Message).Contains("Embedding");
  }

  [Test]
  public async Task Writing_AlwaysWritesTheArrayForm_AsTheDefaultWriterDoesAsync() {
    float[] vector = [0.1f, -2.5f, 1e-38f];

    var written = _write(_arrayConverter(), vector);

    await Assert.That(written).IsEqualTo(JsonSerializer.Serialize(vector, _info<float[]>()))
      .Because("no write packs in this release (V1): the wire and the stored body keep JSON floats");
  }

  [Test]
  public async Task AReadOnlyListVector_ReadsEitherForm_AndWritesTheArrayAsync() {
    var converter = StoredVectorConverters.ForReadOnlyList("alt", _info<IReadOnlyList<float>>());
    float[] vector = [1.5f, -0.0f, float.Epsilon];

    var fromPacked = _read(converter, $"\"{Pack(vector)}\"")!;
    var fromArray = _read(converter, "[1.5,-0,1E-45]")!;
    var written = _write<IReadOnlyList<float>>(converter, vector);

    await Assert.That(_bits(fromPacked)).IsEquivalentTo(_bits(vector));
    await Assert.That(_bits(fromArray)).IsEquivalentTo(_bits(vector));
    await Assert.That(written).IsEqualTo(JsonSerializer.Serialize<IReadOnlyList<float>>(vector, _info<IReadOnlyList<float>>()));
  }

  [Test]
  public async Task AReadOnlyListVector_ThatIsNotWholeBinary32Values_FailsNamingThePropertyAsync() {
    var converter = StoredVectorConverters.ForReadOnlyList("alt", _info<IReadOnlyList<float>>());

    var failure = await Assert.That(() => _read(converter, "\"AAAAAAA=\"")).Throws<JsonException>();

    await Assert.That(failure!.Message).Contains("'alt'");
  }

  [Test]
  public async Task ANullPropertyName_IsRefusedAsync() {
    await Assert.That(() => StoredVectorConverters.ForArray(null!, _info<float[]>())).Throws<ArgumentNullException>();
    await Assert.That(() => StoredVectorConverters.ForReadOnlyList(null!, _info<IReadOnlyList<float>>())).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ANullArrayForm_IsRefusedAsync() {
    await Assert.That(() => StoredVectorConverters.ForArray("v", null!)).Throws<ArgumentNullException>();
    await Assert.That(() => StoredVectorConverters.ForReadOnlyList("v", null!)).Throws<ArgumentNullException>();
  }
}
