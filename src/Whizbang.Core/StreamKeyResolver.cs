using System.Reflection;

namespace Whizbang.Core;

/// <summary>
/// Resolves stream keys from events using [StreamKey] attribute.
/// This reflection-based implementation will be replaced by source-generated code for AOT compatibility.
/// </summary>
public static class StreamKeyResolver {
  /// <summary>
  /// Resolves the stream key from an event.
  /// Looks for a property or parameter marked with [StreamKey] attribute.
  /// </summary>
  /// <param name="event">The event to resolve the stream key from</param>
  /// <returns>The stream key as a string</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown when no [StreamKey] attribute is found, or when the stream key value is null or empty
  /// </exception>
  public static string Resolve(IEvent @event) {
    ArgumentNullException.ThrowIfNull(@event);

    var eventType = @event.GetType();

    // Check properties first
    var properties = eventType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
    foreach (var property in properties) {
      var attribute = property.GetCustomAttribute<StreamKeyAttribute>();
      if (attribute != null) {
        var value = property.GetValue(@event);
        return ValidateAndConvertStreamKey(value, eventType);
      }
    }

    // Check constructor parameters (for records)
    var constructors = eventType.GetConstructors();
    if (constructors.Length > 0) {
      var constructor = constructors[0]; // Primary constructor for records
      var parameters = constructor.GetParameters();

      foreach (var parameter in parameters) {
        var attribute = parameter.GetCustomAttribute<StreamKeyAttribute>();
        if (attribute != null) {
          // For records, the property name matches the parameter name (with capitalization)
          var propertyName = char.ToUpper(parameter.Name![0]) + parameter.Name.Substring(1);
          var property = eventType.GetProperty(propertyName);
          if (property != null) {
            var value = property.GetValue(@event);
            return ValidateAndConvertStreamKey(value, eventType);
          }
        }
      }
    }

    throw new InvalidOperationException(
      $"No [StreamKey] attribute found on event type '{eventType.Name}'. " +
      "Mark a property or parameter with [StreamKey] to identify the stream.");
  }

  private static string ValidateAndConvertStreamKey(object? value, Type eventType) {
    if (value == null) {
      throw new InvalidOperationException(
        $"Stream key value cannot be null for event type '{eventType.Name}'.");
    }

    var stringValue = value.ToString();
    if (string.IsNullOrWhiteSpace(stringValue)) {
      throw new InvalidOperationException(
        $"Stream key value cannot be empty for event type '{eventType.Name}'.");
    }

    return stringValue;
  }
}
