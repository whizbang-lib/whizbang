// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The serializer's side of a dictionary member's stored form: writes an array of key/value objects and reads it back,
/// or an object an earlier release wrote.
/// </summary>
/// <typeparam name="TMember">The declared member type: <c>Dictionary</c>, <c>IDictionary</c> or <c>IReadOnlyDictionary</c>.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <remarks>
/// The atomic upsert writes a perspective document with the serializer and Entity Framework reads it back, so the two
/// must store one form. This converter is what the persistence profile resolves a registered dictionary type to
/// (<see cref="DictionaryMemberForms"/>); Entity Framework's mapping writes and reads through the same
/// <see cref="DictionaryMemberForm{TKey, TValue}"/>.
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/DictionaryMemberFormTests.cs</tests>
public sealed class KeyValueArrayJsonConverter<TMember, TKey, TValue> : JsonConverter<TMember>
    where TMember : class, IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull {
  /// <inheritdoc/>
  public override TMember? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
    (TMember)(object)DictionaryMemberForm<TKey, TValue>.Instance.Read(ref reader, options);

  /// <inheritdoc/>
  public override void Write(Utf8JsonWriter writer, TMember value, JsonSerializerOptions options) =>
    DictionaryMemberForm<TKey, TValue>.Instance.Write(writer, value, options);
}

/// <summary>
/// Answers, on the persistence profile's converter list, for every registered dictionary type with its
/// <see cref="KeyValueArrayJsonConverter{TMember, TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// A converter on the options is what a serializer context consults for a member's type before its own metadata, and
/// what the generated message context asks first for a dictionary. The registry's resolver answers a type asked for on
/// its own; this answers it as a member. Neither constructs a generic type: the form for the type builds its own.
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/DictionaryMemberFormTests.cs</tests>
public sealed class KeyValueArrayJsonConverterFactory : JsonConverterFactory {
  /// <summary>The single instance the registry puts on the persistence profile.</summary>
  public static KeyValueArrayJsonConverterFactory Instance { get; } = new();

  private KeyValueArrayJsonConverterFactory() {
  }

  /// <inheritdoc/>
  public override bool CanConvert(Type typeToConvert) => DictionaryMemberForms.TryGet(typeToConvert, out _);

  /// <inheritdoc/>
  public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
    DictionaryMemberForms.TryGet(typeToConvert, out var form) ? form.CreateConverter(typeToConvert) : null;
}
