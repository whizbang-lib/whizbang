// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
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
/// </remarks>
/// <docs>fundamentals/events/superseding-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/SupersedingEventsTests.cs</tests>
public static class SupersedingEvents {
  private sealed record Registration(Type EventType, Func<object, string?>? Reader);

  private static readonly ConcurrentDictionary<Type, Registration> _byType = new();
  private static readonly ConcurrentDictionary<string, Registration> _byName = new(StringComparer.Ordinal);

  /// <summary>Registers a superseding event type. Called by generated code; registering a type twice is harmless.</summary>
  /// <param name="eventType">The event type.</param>
  /// <param name="keyOf">Reads the key of an event of the type, or <see langword="null"/> when the type declares no
  /// <see cref="Attributes.SupersedingKeyAttribute"/> (one key per stream).</param>
  /// <remarks>The first registration of a type is kept: a type's key is declared once, at compile time.</remarks>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public static void Register(Type eventType, Func<object, string?>? keyOf) {
    ArgumentNullException.ThrowIfNull(eventType);
    var registration = new Registration(eventType, keyOf);
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
  /// <returns>The key, or <see langword="null"/> (see <see cref="KeyOf(object?)"/>).</returns>
  public static string? KeyOf(object? payload, string? wireTypeName, JsonSerializerOptions? jsonOptions) {
    if (payload is not JsonElement json) {
      return KeyOf(payload);
    }
    if (EventFlagsDeriver.ToClrTypeName(wireTypeName) is not { } clrTypeName
        || !_byName.TryGetValue(clrTypeName, out var registration)) {
      return null;
    }
    if (registration.Reader is null) {
      return string.Empty;
    }
    // Read only for a superseding type with a key: the one place a received payload is deserialized before its handler.
    var options = jsonOptions ?? JsonContextRegistry.CreateCombinedOptions();
    var typed = json.Deserialize(options.GetTypeInfo(registration.EventType));
    return typed is null ? null : registration.Reader(typed);
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
}
