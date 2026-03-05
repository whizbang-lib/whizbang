using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using Whizbang.Core.Observability;

namespace Whizbang.Core.AutoPopulate;

/// <summary>
/// Extension methods for retrieving auto-populated values from message envelopes.
/// </summary>
/// <remarks>
/// <para>
/// Auto-populated values are stored in envelope metadata with an "auto:" prefix.
/// These extension methods provide convenient access to those values with proper
/// type deserialization using AOT-compatible JSON serialization.
/// </para>
/// <para>
/// Values are populated by <see cref="AutoPopulateProcessor"/> when messages are
/// dispatched or received, based on attribute decorations on message properties.
/// </para>
/// </remarks>
/// <docs>attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/MessageEnvelopeAutoPopulateExtensionsTests.cs</tests>
public static class MessageEnvelopeAutoPopulateExtensions {
  /// <summary>
  /// Gets an auto-populated value from the envelope metadata.
  /// </summary>
  /// <typeparam name="T">The type to deserialize the value as.</typeparam>
  /// <param name="envelope">The message envelope.</param>
  /// <param name="propertyName">The property name (without the "auto:" prefix).</param>
  /// <returns>The deserialized value, or null if not found.</returns>
  /// <remarks>
  /// <para>
  /// This method looks up metadata with the key "auto:{propertyName}" and deserializes
  /// the stored JsonElement to the requested type.
  /// </para>
  /// <para>
  /// Uses AOT-compatible deserialization for common primitive types (string, int, long,
  /// bool, double, decimal, Guid, DateTime, DateTimeOffset). For complex types, use
  /// <see cref="IMessageEnvelope.GetMetadata"/> directly with your own JsonTypeInfo.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// var sentAt = envelope.GetAutoPopulated&lt;DateTimeOffset&gt;("SentAt");
  /// var userId = envelope.GetAutoPopulated&lt;string&gt;("CreatedBy");
  /// </code>
  /// </example>
  public static T? GetAutoPopulated<T>(this IMessageEnvelope envelope, string propertyName) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(propertyName);

    var key = $"{AutoPopulateProcessor.METADATA_PREFIX}{propertyName}";
    var element = envelope.GetMetadata(key);

    if (!element.HasValue) {
      return default;
    }

    return _deserializeElement<T>(element.Value);
  }

  /// <summary>
  /// Tries to get an auto-populated value from the envelope metadata.
  /// </summary>
  /// <typeparam name="T">The type to deserialize the value as.</typeparam>
  /// <param name="envelope">The message envelope.</param>
  /// <param name="propertyName">The property name (without the "auto:" prefix).</param>
  /// <param name="value">When this method returns, contains the value if found; otherwise, the default value.</param>
  /// <returns>True if the value was found; otherwise, false.</returns>
  public static bool TryGetAutoPopulated<T>(
      this IMessageEnvelope envelope,
      string propertyName,
      [MaybeNullWhen(false)] out T value) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(propertyName);

    var key = $"{AutoPopulateProcessor.METADATA_PREFIX}{propertyName}";
    var element = envelope.GetMetadata(key);

    if (!element.HasValue) {
      value = default;
      return false;
    }

    value = _deserializeElement<T>(element.Value);
    return !EqualityComparer<T>.Default.Equals(value, default);
  }

  /// <summary>
  /// Checks if an auto-populated value exists in the envelope metadata.
  /// </summary>
  /// <param name="envelope">The message envelope.</param>
  /// <param name="propertyName">The property name (without the "auto:" prefix).</param>
  /// <returns>True if the value exists; otherwise, false.</returns>
  public static bool HasAutoPopulated(this IMessageEnvelope envelope, string propertyName) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(propertyName);

    var key = $"{AutoPopulateProcessor.METADATA_PREFIX}{propertyName}";
    return envelope.GetMetadata(key).HasValue;
  }

  /// <summary>
  /// Gets all auto-populated property names from the envelope metadata.
  /// </summary>
  /// <param name="envelope">The message envelope.</param>
  /// <returns>An enumerable of property names (without the "auto:" prefix).</returns>
  /// <remarks>
  /// Scans all current hops for metadata keys starting with "auto:" and returns
  /// the property names with the prefix removed.
  /// </remarks>
  public static IEnumerable<string> GetAllAutoPopulatedKeys(this IMessageEnvelope envelope) {
    ArgumentNullException.ThrowIfNull(envelope);

    const string prefix = AutoPopulateProcessor.METADATA_PREFIX;
    var keys = new HashSet<string>();

    // Scan all current hops for auto-populate metadata
    for (int i = envelope.Hops.Count - 1; i >= 0; i--) {
      var hop = envelope.Hops[i];
      if (hop.Type != HopType.Current || hop.Metadata == null) {
        continue;
      }

      foreach (var key in hop.Metadata.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))) {
        keys.Add(key[prefix.Length..]);
      }
    }

    return keys;
  }

  /// <summary>
  /// Deserializes a JsonElement to the specified type using AOT-compatible serialization.
  /// </summary>
  private static T? _deserializeElement<T>(JsonElement element) {
    // Use AOT-compatible deserialization for common types
    var targetType = typeof(T);
    var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

    // Handle common primitive types with AOT-compatible deserialization
    if (underlyingType == typeof(string)) {
      return (T?)(object?)element.GetString();
    }
    if (underlyingType == typeof(int)) {
      return (T)(object)element.GetInt32();
    }
    if (underlyingType == typeof(long)) {
      return (T)(object)element.GetInt64();
    }
    if (underlyingType == typeof(bool)) {
      return (T)(object)element.GetBoolean();
    }
    if (underlyingType == typeof(double)) {
      return (T)(object)element.GetDouble();
    }
    if (underlyingType == typeof(decimal)) {
      return (T)(object)element.GetDecimal();
    }
    if (underlyingType == typeof(Guid)) {
      return (T)(object)element.GetGuid();
    }
    if (underlyingType == typeof(DateTime)) {
      return (T)(object)element.GetDateTime();
    }
    if (underlyingType == typeof(DateTimeOffset)) {
      return (T)(object)element.GetDateTimeOffset();
    }

    // For unsupported types, return default and log that the type is not supported
    // This maintains AOT compatibility by avoiding reflection-based deserialization
    // Users with complex types should use GetMetadata() directly and deserialize with their own JsonTypeInfo
    return default;
  }
}
