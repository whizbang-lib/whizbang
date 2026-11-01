// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text.Json;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// The event types declared <see cref="Attributes.SupersedingAttribute">superseding</see>, each with how to read its key,
/// registered by every assembly's generated message catalog when the assembly loads. The flag derivation and the key stamp
/// read them here instead of through reflection.
/// </summary>
/// <docs>fundamentals/events/superseding-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/SupersedingEventsTests.cs</tests>
public static class SupersedingEvents {
  /// <summary>Registers a superseding event type. Called by generated code; registering a type twice is harmless.</summary>
  /// <param name="eventType">The event type.</param>
  /// <param name="keyOf">Reads the key of an event of the type, or <see langword="null"/> when the type declares no
  /// <see cref="Attributes.SupersedingKeyAttribute"/> (one key per stream).</param>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public static void Register(Type eventType, Func<object, string?>? keyOf) {
    _ = (eventType, keyOf);  // RED stub
  }

  /// <summary><see langword="true"/> when <paramref name="eventType"/> was registered as superseding.</summary>
  /// <param name="eventType">The event type.</param>
  /// <returns>Whether events of the type supersede each other.</returns>
  public static bool IsSuperseding(Type eventType) => eventType is null;

  /// <summary>The key a typed <paramref name="payload"/> is stored with.</summary>
  /// <param name="payload">The payload, possibly null.</param>
  /// <returns>The key, or <see langword="null"/> when the payload is not superseding or its key is null.</returns>
  public static string? KeyOf(object? payload) => payload as string;

  /// <summary>The key a received payload is stored with, reading a <see cref="JsonElement"/> payload by its type name.</summary>
  /// <param name="payload">The payload, typed or a <see cref="JsonElement"/>.</param>
  /// <param name="wireTypeName">The payload's type name as the transport carried it.</param>
  /// <param name="jsonOptions">The options that read the payload.</param>
  /// <returns>The key, or <see langword="null"/>.</returns>
  public static string? KeyOf(object? payload, string? wireTypeName, JsonSerializerOptions? jsonOptions) =>
    (payload, wireTypeName, jsonOptions) is (string s, _, _) ? s : null;

  /// <summary>A key property's value as the stored key: invariant text, or <see langword="null"/>.</summary>
  /// <param name="value">The property's value.</param>
  /// <returns>The key text.</returns>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public static string? KeyText(object? value) => value as string;
}
