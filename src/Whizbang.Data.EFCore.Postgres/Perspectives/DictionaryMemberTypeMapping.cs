// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Entity Framework's mapping of a dictionary member inside a perspective document: a scalar whose JSON is the
/// dictionary's stored form, an array of key/value objects (#1911).
/// </summary>
/// <remarks>
/// <para>
/// Entity Framework's JSON mapping takes a dictionary for a navigation and refuses the model. Answered by
/// <see cref="DictionaryMemberTypeMappingSourcePlugin"/> for every dictionary type a perspective model holds, it is a
/// value instead, at whatever depth Entity Framework's own walk reaches it: a member, a member of a nested object, a
/// member of a collection element. Its JSON reader and writer go through the same
/// <see cref="DictionaryMemberForm{TKey, TValue}"/> the serializer's converter does, so a document reads the same
/// whichever writer wrote it.
/// </para>
/// <para>
/// The comparer compares and snapshots the stored form, so changing a value in place, adding a key or reordering keys
/// is a change.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryMemberStorageTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryMemberTypeMappingTests.cs</tests>
public sealed class DictionaryMemberTypeMapping : RelationalTypeMapping {
  private DictionaryMemberTypeMapping(RelationalTypeMappingParameters parameters, DictionaryMemberForm form, ValueConverter keyToJson)
    : base(parameters) {
    Form = form;
    KeyToJson = keyToJson;
  }

  /// <summary>The stored form the mapping reads and writes.</summary>
  public DictionaryMemberForm Form { get; }

  /// <summary>Converts a key to its JSON text, as the form stores it: what a key lookup compares a stored key to.</summary>
  public ValueConverter KeyToJson { get; }

  /// <summary>The mapping of a dictionary member type, when the type is a registered form's.</summary>
  /// <param name="memberType">A member's declared type.</param>
  /// <returns>The mapping, or <see langword="null"/> when no form is registered for the type.</returns>
  /// <remarks>One mapping per type for the life of the process: a mapping is immutable.</remarks>
  public static DictionaryMemberTypeMapping? For(Type memberType) {
    ArgumentNullException.ThrowIfNull(memberType);
    if (_built.TryGetValue(memberType, out var existing)) {
      return existing;
    }
    if (!DictionaryMemberForms.TryGet(memberType, out var form)) {
      return null;
    }

    foreach (var mapping in form.Accept(Factory.Instance)) {
      _built.TryAdd(mapping.ClrType, mapping);
    }
    return _built[memberType];
  }

  private static readonly ConcurrentDictionary<Type, DictionaryMemberTypeMapping> _built = new();

  /// <inheritdoc/>
  protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
    new DictionaryMemberTypeMapping(parameters, Form, KeyToJson);

  /// <summary>Builds the three mappings of one form, with its key and value types in hand.</summary>
  private sealed class Factory : IDictionaryMemberFormVisitor<DictionaryMemberTypeMapping[]> {
    public static Factory Instance { get; } = new();

    public DictionaryMemberTypeMapping[] Visit<TKey, TValue>(DictionaryMemberForm<TKey, TValue> form) where TKey : notnull {
      var keyToJson = new ValueConverter<TKey, string>(
        key => form.KeyToJson(key, PerspectiveDocumentSerialization.Options),
        json => form.KeyFromJson(json, PerspectiveDocumentSerialization.Options));
      return [
        _mapping<Dictionary<TKey, TValue>, TKey, TValue>(form, keyToJson),
        _mapping<IDictionary<TKey, TValue>, TKey, TValue>(form, keyToJson),
        _mapping<IReadOnlyDictionary<TKey, TValue>, TKey, TValue>(form, keyToJson),
      ];
    }

    private static DictionaryMemberTypeMapping _mapping<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties)] TMember,
        TKey, TValue>(
        DictionaryMemberForm<TKey, TValue> form, ValueConverter keyToJson)
        where TMember : class, IEnumerable<KeyValuePair<TKey, TValue>>
        where TKey : notnull {
      var converter = new ValueConverter<TMember, string>(
        member => DictionaryMemberValues<TMember, TKey, TValue>.ToJson(member),
        json => DictionaryMemberValues<TMember, TKey, TValue>.FromJson(json));
      var comparer = new ValueComparer<TMember>(
        (left, right) => DictionaryMemberValues<TMember, TKey, TValue>.AreEqual(left, right),
        member => DictionaryMemberValues<TMember, TKey, TValue>.HashOf(member),
        member => DictionaryMemberValues<TMember, TKey, TValue>.Snapshot(member));

      return new DictionaryMemberTypeMapping(
        new RelationalTypeMappingParameters(
          new CoreTypeMappingParameters(
            typeof(TMember),
            converter,
            comparer,
            jsonValueReaderWriter: DictionaryMemberJsonReaderWriter<TMember, TKey, TValue>.Instance),
          storeType: "jsonb"),
        form,
        keyToJson);
    }
  }
}

/// <summary>
/// A dictionary member's value as its stored form: the text, equality, hash and snapshot the mapping's converter and
/// comparer are built from.
/// </summary>
/// <typeparam name="TMember">The declared member type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryMemberTypeMappingTests.cs</tests>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
  Justification = "Entity Framework's converters, comparers and reader/writers are built per closed member type")]
public static class DictionaryMemberValues<TMember, TKey, TValue>
    where TMember : class, IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull {
  private static DictionaryMemberForm<TKey, TValue> _form => DictionaryMemberForm<TKey, TValue>.Instance;

  /// <summary>The stored form's text.</summary>
  /// <param name="member">The dictionary.</param>
  /// <returns>The JSON array of pairs.</returns>
  public static string ToJson(TMember member) => _form.ToJson(member, PerspectiveDocumentSerialization.Options);

  /// <summary>The dictionary a stored form's text holds.</summary>
  /// <param name="json">The JSON array of pairs, or an object an earlier release stored.</param>
  /// <returns>The dictionary.</returns>
  public static TMember FromJson(string json) =>
    (TMember)(object)_form.FromJson(Encoding.UTF8.GetBytes(json), PerspectiveDocumentSerialization.Options);

  /// <summary>Whether two dictionaries store the same: the same pairs in the same order.</summary>
  /// <param name="left">One dictionary, or null.</param>
  /// <param name="right">The other, or null.</param>
  /// <returns>Whether they are equal.</returns>
  public static bool AreEqual(TMember? left, TMember? right) {
    if (left is null || right is null) {
      return left is null && right is null;
    }
    return ReferenceEquals(left, right) || string.Equals(ToJson(left), ToJson(right), StringComparison.Ordinal);
  }

  /// <summary>A hash consistent with <see cref="AreEqual"/>.</summary>
  /// <param name="member">The dictionary.</param>
  /// <returns>The hash.</returns>
  public static int HashOf(TMember member) => StringComparer.Ordinal.GetHashCode(ToJson(member));

  /// <summary>A deep copy, so a value changed in place after the snapshot is seen as a change.</summary>
  /// <param name="member">The dictionary.</param>
  /// <returns>The copy.</returns>
  public static TMember Snapshot(TMember member) => FromJson(ToJson(member));
}

/// <summary>Entity Framework's JSON reader and writer for a dictionary member, through its stored form.</summary>
/// <typeparam name="TMember">The declared member type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <remarks>
/// Entity Framework reads a document from a stream, a buffer at a time, so a value cannot be handed to the serializer
/// as it stands. The reader copies the member's tokens into a buffer of their own, then reads that whole.
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryMemberTypeMappingTests.cs</tests>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
  Justification = "Entity Framework's converters, comparers and reader/writers are built per closed member type")]
public sealed class DictionaryMemberJsonReaderWriter<TMember, TKey, TValue> : JsonValueReaderWriter<TMember>
    where TMember : class, IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull {
  /// <summary>The single instance Entity Framework uses.</summary>
  public static DictionaryMemberJsonReaderWriter<TMember, TKey, TValue> Instance { get; } = new();

  private DictionaryMemberJsonReaderWriter() {
  }

  /// <inheritdoc/>
  public override TMember FromJsonTyped(ref Utf8JsonReaderManager manager, object? existingObject = null) {
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer)) {
      JsonTokenCopy.CopyValue(ref manager, writer);
    }
    return (TMember)(object)DictionaryMemberForm<TKey, TValue>.Instance.FromJson(buffer.WrittenSpan, PerspectiveDocumentSerialization.Options);
  }

  /// <inheritdoc/>
  public override void ToJsonTyped(Utf8JsonWriter writer, TMember value) =>
    DictionaryMemberForm<TKey, TValue>.Instance.Write(writer, value, PerspectiveDocumentSerialization.Options);

  /// <inheritdoc/>
  public override Expression ConstructorExpression =>
    Expression.Property(null, typeof(DictionaryMemberJsonReaderWriter<TMember, TKey, TValue>), nameof(Instance));
}

/// <summary>Copies one JSON value from Entity Framework's buffered reader to a writer.</summary>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryMemberTypeMappingTests.cs</tests>
internal static class JsonTokenCopy {
  /// <summary>
  /// Copies the value the reader is on, through its last token, leaving the reader on that token as Entity
  /// Framework expects of a value reader.
  /// </summary>
  /// <param name="manager">The reader, on the value's first token.</param>
  /// <param name="writer">Where the value is written.</param>
  public static void CopyValue(ref Utf8JsonReaderManager manager, Utf8JsonWriter writer) {
    var depth = 0;
    while (true) {
      depth += _copyToken(ref manager.CurrentReader, writer);
      if (depth == 0) {
        return;
      }
      manager.MoveNext();
    }
  }

  /// <summary>Writes one token and returns how it changed the nesting depth.</summary>
  private static int _copyToken(ref Utf8JsonReader reader, Utf8JsonWriter writer) {
    switch (reader.TokenType) {
      case JsonTokenType.StartObject:
        writer.WriteStartObject();
        return 1;
      case JsonTokenType.StartArray:
        writer.WriteStartArray();
        return 1;
      case JsonTokenType.EndObject:
        writer.WriteEndObject();
        return -1;
      case JsonTokenType.EndArray:
        writer.WriteEndArray();
        return -1;
      case JsonTokenType.PropertyName:
        writer.WritePropertyName(reader.GetString()!);
        return 0;
      case JsonTokenType.String:
        writer.WriteStringValue(reader.GetString());
        return 0;
      case JsonTokenType.Number:
        // Entity Framework's manager reads from a buffer of its own, never a sequence, so the value is a span.
        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
        return 0;
      case JsonTokenType.True:
      case JsonTokenType.False:
        writer.WriteBooleanValue(reader.GetBoolean());
        return 0;
      default:
        // Null is what remains: Entity Framework's reader refuses comments, and None is never a current token.
        writer.WriteNullValue();
        return 0;
    }
  }
}
