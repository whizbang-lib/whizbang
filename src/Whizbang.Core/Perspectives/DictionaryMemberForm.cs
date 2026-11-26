// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// How a dictionary member of a perspective document is stored: an array of key/value objects,
/// <c>[{"Key":..,"Value":..}]</c>, in the dictionary's own order (#1911).
/// </summary>
/// <remarks>
/// <para>
/// Entity Framework's JSON mapping refuses a dictionary: it takes one for a navigation and fails the model build. An
/// array of key/value objects is a shape every reader handles, a query can test with containment, and that keeps the
/// order a JSON object would lose (PostgreSQL's jsonb sorts an object's keys).
/// </para>
/// <para>
/// One form per closed dictionary type, registered by generated code for every dictionary a perspective model holds at
/// any depth (<see cref="DictionaryMemberForms"/>). The form answers for <see cref="Dictionary{TKey, TValue}"/>,
/// <see cref="IDictionary{TKey, TValue}"/> and <see cref="IReadOnlyDictionary{TKey, TValue}"/> of its key and value,
/// the three shapes a model declares, and reads each back as a <see cref="Dictionary{TKey, TValue}"/>.
/// </para>
/// <para>
/// A key and a value are written with the persistence options' own metadata for their types, so a temporal value is the
/// canonical number, an identifier its object form, and a dictionary inside a value is itself an array of pairs: the
/// same bytes the serializer writes anywhere else in the document.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/DictionaryMemberFormTests.cs</tests>
public abstract class DictionaryMemberForm {
  private protected DictionaryMemberForm() {
  }

  /// <summary>The property name of a pair's key.</summary>
  public const string KEY = "Key";

  /// <summary>The property name of a pair's value.</summary>
  public const string VALUE = "Value";

  /// <summary>The dictionary's key type.</summary>
  public abstract Type KeyType { get; }

  /// <summary>The dictionary's value type.</summary>
  public abstract Type ValueType { get; }

  /// <summary>
  /// The member types this form answers for: <c>Dictionary</c>, <c>IDictionary</c> and <c>IReadOnlyDictionary</c> of
  /// its key and value.
  /// </summary>
  public abstract IReadOnlyList<Type> MemberTypes { get; }

  /// <summary>Calls the visitor with this form's key and value types as type arguments.</summary>
  /// <typeparam name="TResult">What the visitor returns.</typeparam>
  /// <param name="visitor">The visitor.</param>
  /// <returns>The visitor's result.</returns>
  /// <remarks>
  /// How a caller that needs the generic types (Entity Framework's mapping) gets them without constructing a generic
  /// type at run time, which a trimmed or native build cannot do.
  /// </remarks>
  public abstract TResult Accept<TResult>(IDictionaryMemberFormVisitor<TResult> visitor);

  /// <summary>The serializer metadata that stores <paramref name="memberType"/> in this form.</summary>
  /// <param name="memberType">One of <see cref="MemberTypes"/>.</param>
  /// <param name="options">The options the metadata belongs to.</param>
  /// <returns>The metadata.</returns>
  internal abstract JsonTypeInfo CreateTypeInfo(Type memberType, JsonSerializerOptions options);

  /// <summary>The converter that stores <paramref name="memberType"/> in this form.</summary>
  /// <param name="memberType">One of <see cref="MemberTypes"/>.</param>
  /// <returns>The converter.</returns>
  internal abstract JsonConverter CreateConverter(Type memberType);
}

/// <summary>Receives a <see cref="DictionaryMemberForm"/> with its key and value types as type arguments.</summary>
/// <typeparam name="TResult">What the visitor returns.</typeparam>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/DictionaryMemberFormTests.cs</tests>
public interface IDictionaryMemberFormVisitor<out TResult> {
  /// <summary>Visits the form for <typeparamref name="TKey"/> and <typeparamref name="TValue"/>.</summary>
  /// <typeparam name="TKey">The key type.</typeparam>
  /// <typeparam name="TValue">The value type.</typeparam>
  /// <param name="form">The form.</param>
  /// <returns>The result.</returns>
  TResult Visit<TKey, TValue>(DictionaryMemberForm<TKey, TValue> form) where TKey : notnull;
}

/// <summary>The stored form of a dictionary of <typeparamref name="TKey"/> to <typeparamref name="TValue"/>.</summary>
/// <typeparam name="TKey">The key type: anything with a stable JSON form.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/DictionaryMemberFormTests.cs</tests>
[global::System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
  Justification = "One form per closed dictionary type: the instance is the type's, and generated code names it by its type arguments")]
public sealed class DictionaryMemberForm<TKey, TValue> : DictionaryMemberForm where TKey : notnull {
  private static readonly Type[] _memberTypes = [
    typeof(Dictionary<TKey, TValue>),
    typeof(IDictionary<TKey, TValue>),
    typeof(IReadOnlyDictionary<TKey, TValue>),
  ];

  /// <summary>The one instance per closed type.</summary>
  public static DictionaryMemberForm<TKey, TValue> Instance { get; } = new();

  private DictionaryMemberForm() {
  }

  /// <inheritdoc/>
  public override Type KeyType => typeof(TKey);

  /// <inheritdoc/>
  public override Type ValueType => typeof(TValue);

  /// <inheritdoc/>
  public override IReadOnlyList<Type> MemberTypes => _memberTypes;

  /// <inheritdoc/>
  public override TResult Accept<TResult>(IDictionaryMemberFormVisitor<TResult> visitor) {
    ArgumentNullException.ThrowIfNull(visitor);
    return visitor.Visit(this);
  }

  /// <inheritdoc/>
  internal override JsonTypeInfo CreateTypeInfo(Type memberType, JsonSerializerOptions options) {
    if (memberType == typeof(Dictionary<TKey, TValue>)) {
      return JsonMetadataServices.CreateValueInfo<Dictionary<TKey, TValue>>(options, new KeyValueArrayJsonConverter<Dictionary<TKey, TValue>, TKey, TValue>());
    }

    return memberType == typeof(IDictionary<TKey, TValue>)
      ? JsonMetadataServices.CreateValueInfo<IDictionary<TKey, TValue>>(options, new KeyValueArrayJsonConverter<IDictionary<TKey, TValue>, TKey, TValue>())
      : JsonMetadataServices.CreateValueInfo<IReadOnlyDictionary<TKey, TValue>>(options, new KeyValueArrayJsonConverter<IReadOnlyDictionary<TKey, TValue>, TKey, TValue>());
  }

  /// <inheritdoc/>
  internal override JsonConverter CreateConverter(Type memberType) {
    if (memberType == typeof(Dictionary<TKey, TValue>)) {
      return new KeyValueArrayJsonConverter<Dictionary<TKey, TValue>, TKey, TValue>();
    }

    return memberType == typeof(IDictionary<TKey, TValue>)
      ? new KeyValueArrayJsonConverter<IDictionary<TKey, TValue>, TKey, TValue>()
      : new KeyValueArrayJsonConverter<IReadOnlyDictionary<TKey, TValue>, TKey, TValue>();
  }

  /// <summary>Writes the pairs as an array of key/value objects, in their order.</summary>
  /// <param name="writer">The writer.</param>
  /// <param name="pairs">The dictionary.</param>
  /// <param name="options">The persistence options, whose metadata writes each key and value.</param>
  public void Write(Utf8JsonWriter writer, IEnumerable<KeyValuePair<TKey, TValue>> pairs, JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(writer);
    ArgumentNullException.ThrowIfNull(pairs);
    ArgumentNullException.ThrowIfNull(options);

    var keyInfo = _typeInfo<TKey>(options);
    var valueInfo = _typeInfo<TValue>(options);
    writer.WriteStartArray();
    foreach (var pair in pairs) {
      writer.WriteStartObject();
      writer.WritePropertyName(KEY);
      JsonSerializer.Serialize(writer, pair.Key, keyInfo);
      writer.WritePropertyName(VALUE);
      JsonSerializer.Serialize(writer, pair.Value, valueInfo);
      writer.WriteEndObject();
    }
    writer.WriteEndArray();
  }

  /// <summary>The pairs as the text of a JSON array, for a parameter or a comparison.</summary>
  /// <param name="pairs">The dictionary.</param>
  /// <param name="options">The persistence options.</param>
  /// <returns>The JSON text.</returns>
  public string ToJson(IEnumerable<KeyValuePair<TKey, TValue>> pairs, JsonSerializerOptions options) {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer)) {
      Write(writer, pairs, options);
    }
    return Encoding.UTF8.GetString(buffer.ToArray());
  }

  /// <summary>A key's JSON text, exactly as <see cref="Write"/> stores it: what a query compares a stored key to.</summary>
  /// <param name="key">The key.</param>
  /// <param name="options">The persistence options.</param>
  /// <returns>The JSON text.</returns>
  public string KeyToJson(TKey key, JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return JsonSerializer.Serialize(key, _typeInfo<TKey>(options));
  }

  /// <summary>The key a key's JSON text holds: the inverse of <see cref="KeyToJson"/>.</summary>
  /// <param name="json">The key's JSON text.</param>
  /// <param name="options">The persistence options.</param>
  /// <returns>The key.</returns>
  public TKey KeyFromJson(string json, JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return JsonSerializer.Deserialize(json, _typeInfo<TKey>(options))
      ?? throw new JsonException("A dictionary key is never null.");
  }

  /// <summary>Reads a dictionary from JSON text.</summary>
  /// <param name="json">The text: an array of pairs or, from an earlier release, an object.</param>
  /// <param name="options">The persistence options.</param>
  /// <returns>The dictionary.</returns>
  public Dictionary<TKey, TValue> FromJson(ReadOnlySpan<byte> json, JsonSerializerOptions options) {
    var reader = new Utf8JsonReader(json);
    reader.Read();
    return Read(ref reader, options);
  }

  /// <summary>
  /// Reads a dictionary positioned at its first token: an array of key/value objects, or an object as an earlier
  /// release stored one.
  /// </summary>
  /// <param name="reader">The reader, on the array's or the object's start.</param>
  /// <param name="options">The persistence options.</param>
  /// <returns>The dictionary, its keys in the order they were stored.</returns>
  /// <exception cref="JsonException">The value is neither form, a pair has no key, or a key appears twice.</exception>
  public Dictionary<TKey, TValue> Read(ref Utf8JsonReader reader, JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    return reader.TokenType switch {
      JsonTokenType.StartArray => _readPairs(ref reader, options),
      JsonTokenType.StartObject => _readObject(ref reader, options),
      _ => throw new JsonException(
        $"A {TypeNameFormatter.ForLog(typeof(Dictionary<TKey, TValue>))} is stored as an array of key/value objects; "
        + $"found {reader.TokenType}."),
    };
  }

  private static Dictionary<TKey, TValue> _readPairs(ref Utf8JsonReader reader, JsonSerializerOptions options) {
    var keyInfo = _typeInfo<TKey>(options);
    var valueInfo = _typeInfo<TValue>(options);
    var result = new Dictionary<TKey, TValue>();

    // A complete value is in the reader, so Read never runs out before the closing token; it throws on a truncated one.
    while (_next(ref reader) != JsonTokenType.EndArray) {
      if (reader.TokenType != JsonTokenType.StartObject) {
        throw new JsonException($"A dictionary pair is an object with {KEY} and {VALUE}; found {reader.TokenType}.");
      }

      var hasKey = false;
      TKey key = default!;
      TValue value = default!;
      while (_next(ref reader) != JsonTokenType.EndObject) {
        var name = reader.GetString();
        reader.Read();
        if (string.Equals(name, KEY, StringComparison.OrdinalIgnoreCase)) {
          key = JsonSerializer.Deserialize(ref reader, keyInfo)
            ?? throw new JsonException("A dictionary pair has a null key.");
          hasKey = true;
        } else if (string.Equals(name, VALUE, StringComparison.OrdinalIgnoreCase)) {
          value = JsonSerializer.Deserialize(ref reader, valueInfo)!;
        } else {
          reader.Skip();
        }
      }

      if (!hasKey) {
        throw new JsonException($"A dictionary pair has no {KEY}.");
      }
      _add(result, key, value);
    }

    return result;
  }

  private static Dictionary<TKey, TValue> _readObject(ref Utf8JsonReader reader, JsonSerializerOptions options) {
    StoredFormFallbacks.ObjectFormDictionaryRead(typeof(Dictionary<TKey, TValue>));

    var valueInfo = _typeInfo<TValue>(options);
    var result = new Dictionary<TKey, TValue>();
    while (_next(ref reader) != JsonTokenType.EndObject) {
      var key = _keyFromName(reader.GetString()!, options);
      reader.Read();
      _add(result, key, JsonSerializer.Deserialize(ref reader, valueInfo)!);
    }

    return result;
  }

  /// <summary>
  /// A key from a property name, as the serializer wrote a dictionary's key: the text itself for a string, the
  /// key's JSON string form (an identifier, a date), its bare form (a number, a boolean) or an enumeration's name.
  /// </summary>
  private static TKey _keyFromName(string name, JsonSerializerOptions options) {
    if (typeof(TKey) == typeof(string)) {
      return (TKey)(object)name;
    }

    var keyInfo = _typeInfo<TKey>(options);
    var quoted = JsonSerializer.Serialize(name, _typeInfo<string>(options));
    if (_tryDeserialize(quoted, keyInfo, out var fromString)) {
      return fromString;
    }
    if (_tryDeserialize(name, keyInfo, out var bare)) {
      return bare;
    }
    if (typeof(TKey).IsEnum && Enum.TryParse(typeof(TKey), name, ignoreCase: false, out var named)) {
      return (TKey)named;
    }

    throw new JsonException($"The stored key '{name}' is not a {TypeNameFormatter.ForLog(typeof(TKey))}.");
  }

  private static bool _tryDeserialize(string json, JsonTypeInfo<TKey> keyInfo, out TKey key) {
    try {
      key = JsonSerializer.Deserialize(json, keyInfo)!;
      return key is not null;
    } catch (JsonException) {
      key = default!;
      return false;
    }
  }

  private static void _add(Dictionary<TKey, TValue> result, TKey key, TValue value) {
    if (!result.TryAdd(key, value)) {
      throw new JsonException($"A stored dictionary names the duplicate key '{key}'; each key is stored once.");
    }
  }

  private static JsonTokenType _next(ref Utf8JsonReader reader) {
    reader.Read();
    return reader.TokenType;
  }

  private static JsonTypeInfo<T> _typeInfo<T>(JsonSerializerOptions options) =>
    (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}
