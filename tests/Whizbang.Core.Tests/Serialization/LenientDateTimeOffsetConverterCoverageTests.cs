using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Covers <see cref="LenientNullableDateTimeOffsetConverter"/>'s own null-handling body — code
/// that <c>System.Text.Json</c>'s serializer never actually calls. Because the converter's generic
/// parameter IS the nullable type (<c>DateTimeOffset?</c>) and it does not override
/// <c>HandleNull</c>, <c>JsonConverter&lt;T&gt;</c>'s default (<c>HandleNullOnRead</c> false for a
/// type whose default is null; <c>HandleNullOnWrite</c> always false) makes STJ intercept null
/// tokens/values itself and never invoke <see cref="LenientNullableDateTimeOffsetConverter.Read"/>
/// or <see cref="LenientNullableDateTimeOffsetConverter.Write"/> at all — confirmed against the
/// runtime's own <c>JsonConverterOfT.TryRead</c>/<c>TryWrite</c> null short-circuits. The sibling
/// <c>Read_Null_ReturnsNullAsync</c>/<c>Write_Null_WritesNullAsync</c> tests in
/// <c>LenientDateTimeOffsetConverterTests</c> go through <c>JsonSerializer</c> and so — despite
/// appearances — never reach these two lines; only a DIRECT call to the converter does.
/// </summary>
public class LenientDateTimeOffsetConverterCoverageTests {

  /// <summary>
  /// If this line were removed (or started delegating to the inner converter without the null
  /// guard), a direct caller of this converter — e.g. a hand-rolled reader that composes converters
  /// itself rather than going through the top-level JsonSerializer null short-circuit — would crash
  /// parsing a JSON null instead of getting back a null DateTimeOffset.
  /// </summary>
  [Test]
  public async Task Read_CalledDirectlyOnANullToken_ReturnsNullAsync() {
    var converter = new LenientNullableDateTimeOffsetConverter();
    var options = new JsonSerializerOptions();
    var bytes = "null"u8.ToArray();
    var reader = new Utf8JsonReader(bytes);
    reader.Read(); // position on the Null token, exactly as STJ would before invoking a converter

    var result = converter.Read(ref reader, typeof(DateTimeOffset?), options);

    await Assert.That(result).IsNull()
      .Because("a converter over Nullable<DateTimeOffset> must handle a null token itself when "
             + "invoked directly, not only rely on the serializer's own null short-circuit");
  }

  /// <summary>
  /// If this line were removed, a direct caller of this converter's Write method with a null value
  /// would fall through to unwrapping <c>value.Value</c> on a null Nullable&lt;DateTimeOffset&gt;
  /// and throw <see cref="InvalidOperationException"/> instead of writing a JSON null.
  /// </summary>
  [Test]
  public async Task Write_CalledDirectlyWithNullValue_WritesJsonNullAsync() {
    var converter = new LenientNullableDateTimeOffsetConverter();
    var options = new JsonSerializerOptions();
    using var stream = new MemoryStream();
    await using (var writer = new Utf8JsonWriter(stream)) {
      converter.Write(writer, null, options);
    }

    var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());

    await Assert.That(json).IsEqualTo("null")
      .Because("a converter over Nullable<DateTimeOffset> must write a JSON null itself when "
             + "invoked directly with a null value, not only rely on the serializer's own null "
             + "short-circuit");
  }
}
