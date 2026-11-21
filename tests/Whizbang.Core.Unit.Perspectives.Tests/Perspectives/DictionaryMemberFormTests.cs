// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging.Testing;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>A complex dictionary value.</summary>
internal sealed class FormLine {
  public string Name { get; set; } = string.Empty;
  public DateTime At { get; set; }
}

/// <summary>A key type with no string form, so a stored name cannot be read back as one.</summary>
internal sealed record FormRecordKey(int Number);

internal enum FormColor {
  Red = 1,
  Green = 2,
}

/// <summary>Every shape a model declares a dictionary in.</summary>
internal sealed class FormModel {
  public Dictionary<string, int> Counts { get; set; } = [];
  public Dictionary<Guid, FormLine>? Lines { get; set; }
  public IDictionary<int, string> ByNumber { get; set; } = new Dictionary<int, string>();
  public IReadOnlyDictionary<FormColor, string> ByColor { get; set; } = new Dictionary<FormColor, string>();
  public Dictionary<string, Dictionary<string, int>> Nested { get; set; } = [];
  public Dictionary<string, DateTime> Times { get; set; } = [];
}

[JsonSerializable(typeof(FormModel))]
[JsonSerializable(typeof(Dictionary<FormRecordKey, int>))]
internal sealed partial class FormJsonContext : JsonSerializerContext;

/// <summary>
/// A dictionary member's stored form on the serializer's side: an array of key/value objects in the dictionary's
/// order, read back as written, an object an earlier release wrote still read, a duplicate key refused (#1911).
/// </summary>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
public class DictionaryMemberFormTests {
  private static readonly DateTime _at = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
  private static readonly Guid _lineId = Guid.Parse("0190f0a0-0000-7000-8000-000000000001");

  static DictionaryMemberFormTests() {
    DictionaryMemberForms.Register<string, int>();
    DictionaryMemberForms.Register<Guid, FormLine>();
    DictionaryMemberForms.Register<int, string>();
    DictionaryMemberForms.Register<FormColor, string>();
    DictionaryMemberForms.Register<string, Dictionary<string, int>>();
    DictionaryMemberForms.Register<string, DateTime>();
    DictionaryMemberForms.Register<FormRecordKey, int>();
  }

  /// <summary>The options the upsert resolves, with the model's metadata combined in as a caller's would be.</summary>
  private static JsonSerializerOptions _options(SerializationProfile profile = SerializationProfile.Persistence) {
    var union = JsonContextRegistry.CreateCombinedOptions(profile);
    return new JsonSerializerOptions(union) {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(union.TypeInfoResolver!, FormJsonContext.Default),
    };
  }

  private static FormModel _model() => new() {
    Counts = new() { ["zulu"] = 26, ["alpha"] = 1 },
    Lines = new() { [_lineId] = new FormLine { Name = "widget", At = _at } },
    ByNumber = new Dictionary<int, string> { [2] = "two", [1] = "one" },
    ByColor = new Dictionary<FormColor, string> { [FormColor.Green] = "go" },
    Nested = new() { ["outer"] = new() { ["inner"] = 7 } },
    Times = new() { ["at"] = _at },
  };

  private static JsonElement _written(JsonSerializerOptions options) =>
    JsonElement.Parse(JsonSerializer.Serialize(_model(), options));

  private static Dictionary<TKey, TValue> _read<TKey, TValue>(string json) where TKey : notnull =>
    DictionaryMemberForm<TKey, TValue>.Instance.FromJson(Encoding.UTF8.GetBytes(json), _options());

  [Test]
  public async Task EveryDeclaredShape_IsWrittenAsAnArrayOfPairsInItsOrderAsync() {
    var written = _written(_options());

    await Assert.That(written.GetProperty("Counts").GetRawText()).IsEqualTo("""[{"Key":"zulu","Value":26},{"Key":"alpha","Value":1}]""");
    await Assert.That(written.GetProperty("ByNumber").GetRawText()).IsEqualTo("""[{"Key":2,"Value":"two"},{"Key":1,"Value":"one"}]""");
    await Assert.That(written.GetProperty("ByColor").GetRawText()).IsEqualTo("""[{"Key":2,"Value":"go"}]""");
    await Assert.That(written.GetProperty("Lines")[0].GetProperty("Key").GetString()).IsEqualTo(_lineId.ToString());
  }

  [Test]
  public async Task AValue_IsWrittenAsTheDocumentWritesItAnywhereElseAsync() {
    var written = _written(_options());

    await Assert.That(written.GetProperty("Times")[0].GetProperty("Value").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_at))
      .Because("a temporal value is the canonical number, as it is everywhere in a document");
    await Assert.That(written.GetProperty("Lines")[0].GetProperty("Value").GetProperty("At").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(written.GetProperty("Nested")[0].GetProperty("Value").GetRawText()).IsEqualTo("""[{"Key":"inner","Value":7}]""")
      .Because("a dictionary held as a value is itself stored as pairs");
  }

  [Test]
  public async Task TheDefaultProfile_KeepsADictionaryAnObjectAsync() {
    var written = _written(_options(SerializationProfile.Default));

    await Assert.That(written.GetProperty("Counts").ValueKind).IsEqualTo(JsonValueKind.Object)
      .Because("events and the wire are not perspective documents and keep their form");
  }

  [Test]
  public async Task WhatIsWritten_ReadsBackInOrderAsync() {
    var options = _options();
    var read = JsonSerializer.Deserialize<FormModel>(JsonSerializer.Serialize(_model(), options), options)!;

    await Assert.That(string.Join(",", read.Counts.Keys)).IsEqualTo("zulu,alpha");
    await Assert.That(read.Lines![_lineId].At).IsEqualTo(_at);
    await Assert.That(string.Join(",", read.ByNumber.Keys)).IsEqualTo("2,1");
    await Assert.That(read.ByColor[FormColor.Green]).IsEqualTo("go");
    await Assert.That(read.Nested["outer"]["inner"]).IsEqualTo(7);
    await Assert.That(read.Times["at"]).IsEqualTo(_at);
  }

  [Test]
  public async Task NullAndEmpty_StayDifferentAsync() {
    var options = _options();
    var model = _model();
    model.Lines = null;
    model.Counts = [];

    var read = JsonSerializer.Deserialize<FormModel>(JsonSerializer.Serialize(model, options), options)!;

    await Assert.That(read.Lines).IsNull();
    await Assert.That(read.Counts.Count).IsEqualTo(0);
  }

  [Test]
  public async Task TheObjectFormAnEarlierReleaseWrote_StillReadsAsync() {
    var options = _options();
    var json = $$$"""
      {"Counts":{"zulu":26,"alpha":1},"Lines":{"{{{_lineId}}}":{"Name":"widget","At":0}},
       "ByNumber":{"2":"two"},"ByColor":{"Green":"go","1":"one"},"Nested":{"outer":{"inner":7}},"Times":{}}
      """;

    var read = JsonSerializer.Deserialize<FormModel>(json, options)!;

    await Assert.That(read.Counts["alpha"]).IsEqualTo(1);
    await Assert.That(read.Lines![_lineId].Name).IsEqualTo("widget");
    await Assert.That(read.ByNumber[2]).IsEqualTo("two");
    await Assert.That(read.ByColor[FormColor.Green]).IsEqualTo("go");
    await Assert.That(read.ByColor[FormColor.Red]).IsEqualTo("one");
    await Assert.That(read.Nested["outer"]["inner"]).IsEqualTo(7);
  }

  [Test]
  public async Task AStoredNameThatIsNotAKey_IsRefusedAsync() {
    var failure = await Assert.That(() => _read<FormRecordKey, int>("""{"null":1}""")).Throws<JsonException>();

    await Assert.That(failure!.Message).Contains("is not a");
  }

  [Test]
  [Arguments("""[{"Key":"a","Value":1},{"Key":"a","Value":2}]""", "duplicate key 'a'")]
  [Arguments("""[{"Value":1}]""", "has no Key")]
  [Arguments("[1]", "is an object with Key and Value")]
  [Arguments("""[{"Key":null,"Value":1}]""", "null key")]
  [Arguments("""{"a":1,"a":2}""", "duplicate key 'a'")]
  [Arguments("42", "found Number")]
  public async Task AMalformedStoredDictionary_IsRefusedAsync(string json, string reason) {
    var failure = await Assert.That(() => _read<string, int>(json)).Throws<JsonException>();

    await Assert.That(failure!.Message).Contains(reason);
  }

  [Test]
  public async Task AnUnknownPairMember_IsSkippedAsync() {
    var read = _read<string, int>("""[{"Note":{"x":[1]},"Key":"a","Value":3}]""");

    await Assert.That(read["a"]).IsEqualTo(3);
  }

  [Test]
  public async Task AKey_RoundTripsThroughItsJsonAsync() {
    var form = DictionaryMemberForm<Guid, FormLine>.Instance;

    var json = form.KeyToJson(_lineId, _options());

    await Assert.That(json).IsEqualTo($"\"{_lineId}\"");
    await Assert.That(form.KeyFromJson(json, _options())).IsEqualTo(_lineId);
    await Assert.That(() => DictionaryMemberForm<string, int>.Instance.KeyFromJson("null", _options())).Throws<JsonException>();
  }

  [Test]
  public async Task TheFormDescribesItsTypesAsync() {
    DictionaryMemberForm form = DictionaryMemberForm<Guid, FormLine>.Instance;

    await Assert.That(form.KeyType).IsEqualTo(typeof(Guid));
    await Assert.That(form.ValueType).IsEqualTo(typeof(FormLine));
    await Assert.That(form.MemberTypes).IsEquivalentTo(new[] {
      typeof(Dictionary<Guid, FormLine>), typeof(IDictionary<Guid, FormLine>), typeof(IReadOnlyDictionary<Guid, FormLine>),
    });
    await Assert.That(form.Accept(new TypeArgumentsVisitor())).IsEqualTo("Guid->FormLine");
    await Assert.That(() => form.Accept<string>(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheWritersRefuseANullArgumentAsync() {
    var form = DictionaryMemberForm<string, int>.Instance;
    await using var buffer = new MemoryStream();
    await using var writer = new Utf8JsonWriter(buffer);

    await Assert.That(() => form.Write(null!, [], _options())).Throws<ArgumentNullException>();
    await Assert.That(() => form.Write(writer, null!, _options())).Throws<ArgumentNullException>();
    await Assert.That(() => form.Write(writer, [], null!)).Throws<ArgumentNullException>();
    await Assert.That(() => form.KeyToJson("a", null!)).Throws<ArgumentNullException>();
    await Assert.That(() => form.KeyFromJson("\"a\"", null!)).Throws<ArgumentNullException>();
    await Assert.That(() => form.FromJson("[]"u8, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheRegistryAnswersEachDeclaredShapeOnceAsync() {
    DictionaryMemberForms.Register<string, int>();

    await Assert.That(DictionaryMemberForms.TryGet(typeof(IReadOnlyDictionary<string, int>), out var form)).IsTrue();
    await Assert.That(form).IsSameReferenceAs(DictionaryMemberForm<string, int>.Instance);
    await Assert.That(DictionaryMemberForms.TryGet(typeof(SortedDictionary<string, int>), out _)).IsFalse();
    await Assert.That(DictionaryMemberForms.Registered.Count(f => f.KeyType == typeof(string) && f.ValueType == typeof(int))).IsEqualTo(1);
    await Assert.That(() => DictionaryMemberForms.TryGet(null!, out _)).Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Both ways the persistence profile is asked for a dictionary answer with the pairs: the registry's resolver, for a
  /// type asked for on its own, and the converter factory, for a member's type.
  /// </summary>
  [Test]
  [Arguments(typeof(Dictionary<int, string>))]
  [Arguments(typeof(IDictionary<int, string>))]
  [Arguments(typeof(IReadOnlyDictionary<int, string>))]
  public async Task EachDeclaredShape_ResolvesToThePairsAsync(Type memberType) {
    var options = _options();
    var factory = KeyValueArrayJsonConverterFactory.Instance;

    await Assert.That(options.GetTypeInfo(memberType).Converter.GetType().Name).StartsWith("KeyValueArrayJsonConverter");
    await Assert.That(factory.CanConvert(memberType)).IsTrue();
    await Assert.That(factory.CreateConverter(memberType, options)!.GetType().Name).StartsWith("KeyValueArrayJsonConverter");
    await Assert.That(options.Converters).Contains(factory);
  }

  [Test]
  public async Task TheFactory_AnswersNothingForAnUnregisteredTypeAsync() {
    var factory = KeyValueArrayJsonConverterFactory.Instance;

    await Assert.That(factory.CanConvert(typeof(SortedDictionary<int, string>))).IsFalse();
    await Assert.That(factory.CreateConverter(typeof(SortedDictionary<int, string>), _options())).IsNull();
  }

  /// <summary>An object-form read is counted by type and announced once per type.</summary>
  [Test]
  [NotInParallel]
  public async Task AnObjectFormReadIsCountedAndAnnouncedAsync() {
    var probeType = TypeNameFormatter.ForLog(typeof(Dictionary<int, Version>));
    var measurements = new List<string?>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == StoredFormFallbacks.METER_NAME
          && instrument.Name == StoredFormFallbacks.DICTIONARY_INSTRUMENT_NAME) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, _, tags, _) => {
      foreach (var tag in tags) {
        if (tag.Key == "type" && Equals(tag.Value, probeType)) {
          measurements.Add(tag.Value?.ToString());
        }
      }
    });
    listener.Start();
    var logger = new FakeLogger();
    StoredFormFallbacks.Configure(meterFactory: null, logger: logger);

    StoredFormFallbacks.ObjectFormDictionaryRead(typeof(Dictionary<int, Version>));
    StoredFormFallbacks.ObjectFormDictionaryRead(typeof(Dictionary<int, Version>));

    await Assert.That(measurements.Count).IsEqualTo(2);
    var announced = logger.Collector.GetSnapshot().Where(r => r.Message.Contains(probeType, StringComparison.Ordinal)).ToList();
    await Assert.That(announced.Count).IsEqualTo(1);
    await Assert.That(announced[0].Message).Contains(StoredFormFallbacks.DICTIONARY_INSTRUMENT_NAME);
    await Assert.That(() => StoredFormFallbacks.ObjectFormDictionaryRead(null!)).Throws<ArgumentNullException>();
  }

  private sealed class TypeArgumentsVisitor : IDictionaryMemberFormVisitor<string> {
    public string Visit<TKey, TValue>(DictionaryMemberForm<TKey, TValue> form) where TKey : notnull =>
      $"{TypeNameFormatter.ForLog(typeof(TKey))}->{TypeNameFormatter.ForLog(typeof(TValue))}";
  }
}
