// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// The event types declared <see cref="Attributes.SupersedingAttribute">superseding</see>, each with how to read its key,
/// registered by every assembly's generated message catalog when the assembly loads. The flag derivation and the key stamp
/// read them here instead of through reflection, so a superseding event is flagged and keyed the same way under native
/// AOT.
/// </summary>
/// <remarks>
/// The key is what <c>wh_event_store.supersede_key</c> holds: the <see cref="Attributes.SupersedingKeyAttribute"/>
/// property's value as invariant text (<see cref="KeyText"/>), the empty string for a superseding type with no key
/// property (one key per stream), and <see langword="null"/> for an event that is not superseding or whose key property is
/// null. The reaper only ever considers events with a key, so a null key keeps the event.
/// <para>
/// A key read from JSON (a received or stored payload) is null when the payload does not carry the key property or
/// carries it as JSON null (#2123): reading the payload as its type would turn an absent value-type key into the type's
/// default, so every such event of a stream would share one key and all but the newest be reaped. A present default
/// (<see cref="Guid.Empty"/>, <c>0</c>) is a real key.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/superseding-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/SupersedingEventsTests.cs</tests>
public static class SupersedingEvents {
  private sealed record Registration(Type EventType, Func<object, string?>? Reader, string? KeyMember, string? KeyJsonName);

  private static readonly ConcurrentDictionary<Type, Registration> _byType = new();
  private static readonly ConcurrentDictionary<string, Registration> _byName = new(StringComparer.Ordinal);

  // Per options instance, the JSON name each keyed type's key is written under (null when no JSON property maps to it).
  // Weak, so options built per call (the combined registered contexts) are not kept alive by the cache.
  private static readonly ConditionalWeakTable<JsonSerializerOptions, ConcurrentDictionary<Type, string?>> _jsonNames = new();

  /// <summary>Registers a superseding event type. Called by generated code; registering a type twice is harmless.</summary>
  /// <param name="eventType">The event type.</param>
  /// <param name="keyOf">Reads the key of an event of the type, or <see langword="null"/> when the type declares no
  /// <see cref="Attributes.SupersedingKeyAttribute"/> (one key per stream).</param>
  /// <remarks>The first registration of a type is kept: a type's key is declared once, at compile time.</remarks>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public static void Register(Type eventType, Func<object, string?>? keyOf) => Register(eventType, keyOf, null, null);

  /// <summary>
  /// Registers a superseding event type with its key member, so a key missing from a JSON payload is told from a default
  /// key (#2123). Called by generated code; registering a type twice is harmless.
  /// </summary>
  /// <param name="eventType">The event type.</param>
  /// <param name="keyOf">Reads the key of an event of the type, or <see langword="null"/> when the type declares no
  /// <see cref="Attributes.SupersedingKeyAttribute"/> (one key per stream).</param>
  /// <param name="keyMember">The CLR name of the key property, or <see langword="null"/> when there is none or it is not
  /// known (the key is then read from JSON by deserializing alone).</param>
  /// <param name="keyJsonName">The key property's own <c>[JsonPropertyName]</c>, or <see langword="null"/>: the name
  /// metadata without member information writes the key under, with <paramref name="keyMember"/> when it is null.</param>
  /// <remarks>The first registration of a type is kept: a type's key is declared once, at compile time.</remarks>
  /// <docs>fundamentals/events/superseding-events#a-key-missing-from-the-payload</docs>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/SupersedingEventsTests.cs:KeyOf_ARegistrationWithoutAMappableKeyMember_ReadsThePayloadAsBeforeAsync</tests>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public static void Register(Type eventType, Func<object, string?>? keyOf, string? keyMember, string? keyJsonName) {
    ArgumentNullException.ThrowIfNull(eventType);
    var registration = new Registration(eventType, keyOf, keyMember, keyJsonName);
    if (_byType.TryAdd(eventType, registration)) {
      // The catalog's ClrTypeName form ("Ns.Outer+Nested"), which is what a received wire type name reduces to.
      _byName.TryAdd(TypeNameFormatter.FormatClrTypeName(eventType), registration);
    }
  }

  /// <summary><see langword="true"/> when <paramref name="eventType"/> was registered as superseding.</summary>
  /// <param name="eventType">The event type.</param>
  /// <returns>Whether events of the type supersede each other.</returns>
  public static bool IsSuperseding(Type eventType) {
    ArgumentNullException.ThrowIfNull(eventType);
    return _byType.ContainsKey(eventType);
  }

  /// <summary>The event types registered as superseding, as a snapshot.</summary>
  /// <returns>Every registered superseding type; the history reconcile walks the stored events of each (#2110).</returns>
  /// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/SupersedingEventsTests.cs:Types_ListsEveryRegisteredSupersedingType_AndNoOtherAsync</tests>
  public static IReadOnlyList<Type> Types() => [.. _byType.Keys];

  /// <summary>The key a typed <paramref name="payload"/> is stored with: the dispatch and direct-append paths.</summary>
  /// <param name="payload">The payload, possibly null.</param>
  /// <returns>The key; the empty string for a superseding type with no key property; <see langword="null"/> when the
  /// payload is not superseding or its key is null.</returns>
  public static string? KeyOf(object? payload) =>
    payload is not null && _byType.TryGetValue(payload.GetType(), out var registration) ? _keyOf(registration, payload) : null;

  /// <summary>
  /// The key a received payload is stored with. A received payload is usually still a <see cref="JsonElement"/>, so the
  /// type is found by <paramref name="wireTypeName"/> and the payload is read as that type before its key is taken, the
  /// same accessor the producer used; a typed payload is read directly.
  /// </summary>
  /// <param name="payload">The payload, typed or a <see cref="JsonElement"/>.</param>
  /// <param name="wireTypeName">The payload's type name as the transport carried it.</param>
  /// <param name="jsonOptions">The options that read the payload; when null, the combined registered contexts.</param>
  /// <returns>The key, or <see langword="null"/> (see <see cref="KeyOf(object?)"/>); also <see langword="null"/> when a
  /// JSON payload does not carry the key property or carries it as JSON null (see <see cref="IsKeyMissing"/>).</returns>
  /// <docs>fundamentals/events/superseding-events#a-key-missing-from-the-payload</docs>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/SupersedingEventsTests.cs:KeyOf_AValueTypeKeyMissingFromTheJson_IsNull_ButAPresentDefaultIsAKeyAsync</tests>
  public static string? KeyOf(object? payload, string? wireTypeName, JsonSerializerOptions? jsonOptions) {
    if (payload is not JsonElement json) {
      return KeyOf(payload);
    }
    if (_keyed(wireTypeName) is not { } registration) {
      return _registered(wireTypeName) is null ? null : string.Empty;
    }
    // Read only for a superseding type with a key: the one place a received payload is deserialized before its handler.
    var options = jsonOptions ?? JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = options.GetTypeInfo(registration.EventType);
    if (_isMissing(json, registration, typeInfo)) {
      return null;
    }
    var typed = json.Deserialize(typeInfo);
    return typed is null ? null : registration.Reader!(typed);
  }

  /// <summary>
  /// <see langword="true"/> when a JSON <paramref name="payload"/> of a superseding type with a key property does not
  /// carry that property, or carries it as JSON null (#2123): its key is unknown, so the event is stored without one and
  /// never superseded. A present default value is not missing.
  /// </summary>
  /// <param name="payload">The payload as JSON.</param>
  /// <param name="wireTypeName">The payload's type name as the transport carried it or the store holds it.</param>
  /// <param name="jsonOptions">The options that read the payload; when null, the combined registered contexts.</param>
  /// <returns>Whether the key is missing; <see langword="false"/> for a type that is not superseding or has no key
  /// property, for a payload that is not a JSON object, and for a registration whose key member no JSON property of the
  /// type maps to (its key is read by deserializing, as before).</returns>
  /// <docs>fundamentals/events/superseding-events#a-key-missing-from-the-payload</docs>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/SupersedingEventsTests.cs:IsKeyMissing_IsFalse_WhereThereIsNoKeyToMissAsync</tests>
  public static bool IsKeyMissing(JsonElement payload, string? wireTypeName, JsonSerializerOptions? jsonOptions) {
    if (_keyed(wireTypeName) is not { } registration) {
      return false;
    }
    var options = jsonOptions ?? JsonContextRegistry.CreateCombinedOptions();
    return _isMissing(payload, registration, options.GetTypeInfo(registration.EventType));
  }

  /// <summary>
  /// A key property's value as the stored key: a string as is, a formattable value (a number, a <see cref="Guid"/>, a
  /// date) in its invariant form, anything else by <see cref="object.ToString"/>, and <see langword="null"/> as null.
  /// </summary>
  /// <param name="value">The property's value.</param>
  /// <returns>The key text.</returns>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public static string? KeyText(object? value) => value switch {
    null => null,
    string text => text,
    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
    _ => value.ToString(),
  };

  private static string? _keyOf(Registration registration, object payload) =>
    registration.Reader is null ? string.Empty : registration.Reader(payload);

  // The registration a wire type name names, if any.
  private static Registration? _registered(string? wireTypeName) =>
    EventFlagsDeriver.ToClrTypeName(wireTypeName) is { } clrTypeName && _byName.TryGetValue(clrTypeName, out var registration)
      ? registration
      : null;

  // The registration a wire type name names when its type has a key property.
  private static Registration? _keyed(string? wireTypeName) =>
    _registered(wireTypeName) is { Reader: not null } registration ? registration : null;

  // Whether an object payload lacks its key property or holds it as null. Undecidable (not an object, or no JSON property
  // maps to the key member) reads as present, so the key is read by deserializing as before.
  private static bool _isMissing(JsonElement payload, Registration registration, JsonTypeInfo typeInfo) {
    if (payload.ValueKind != JsonValueKind.Object || _jsonNameOf(registration, typeInfo) is not { } name) {
      return false;
    }
    var comparison = typeInfo.Options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    foreach (var property in payload.EnumerateObject()) {
      if (string.Equals(property.Name, name, comparison)) {
        return property.Value.ValueKind == JsonValueKind.Null;
      }
    }
    return true;
  }

  private static string? _jsonNameOf(Registration registration, JsonTypeInfo typeInfo) =>
    _jsonNames.GetValue(typeInfo.Options, static _ => new ConcurrentDictionary<Type, string?>())
      .GetOrAdd(registration.EventType, static (_, state) => _resolveJsonName(state.registration, state.typeInfo), (registration, typeInfo));

  // The JSON name the key member is written under. Metadata that knows its members (the reflection resolver, the
  // System.Text.Json source generator) maps the member through its attribute provider, so [JsonPropertyName] and the
  // naming policy apply. The framework's generated contexts carry no attribute provider and write a member under its
  // [JsonPropertyName] or its own name, which the generator registered. No match: null, and the key is read as before.
  private static string? _resolveJsonName(Registration registration, JsonTypeInfo typeInfo) {
    // A type that is not read as an object lists no properties, so it maps nothing either.
    if (registration.KeyMember is not { } member) {
      return null;
    }
    var written = registration.KeyJsonName ?? member;
    string? unattributed = null;
    foreach (var property in typeInfo.Properties) {
      if (property.AttributeProvider is MemberInfo declared) {
        if (string.Equals(declared.Name, member, StringComparison.Ordinal)) {
          return property.Name;
        }
      } else if (string.Equals(property.Name, written, StringComparison.Ordinal)) {
        unattributed = property.Name;
      }
    }
    return unattributed;
  }
}
