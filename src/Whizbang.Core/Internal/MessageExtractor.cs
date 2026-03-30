using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Internal;

/// <summary>
/// Utility for extracting messages from complex return types.
/// Handles single messages, arrays, enumerables, tuples, and nested structures.
/// Used by Dispatcher to automatically capture messages from receptor return values.
/// Extracts both IEvent and ICommand instances (anything implementing IMessage).
/// </summary>
/// <docs>fundamentals/dispatcher/dispatcher#automatic-message-cascade</docs>
public static class MessageExtractor {
  /// <summary>
  /// Extracts all IMessage instances (events and commands) from a potentially complex return value.
  /// Supports: single IMessage, IMessage[], IEnumerable&lt;IMessage&gt;, tuples (Tuple and ValueTuple), and nested structures.
  /// Non-message values are ignored.
  /// </summary>
  /// <param name="result">The result to extract messages from</param>
  /// <returns>Flattened collection of all messages found</returns>
  /// <docs>fundamentals/dispatcher/dispatcher#automatic-message-cascade</docs>
  /// <tests>tests/Whizbang.Core.Tests/Internal/MessageExtractorTests.cs</tests>
  public static IEnumerable<IMessage> ExtractMessages(object? result, Action<Type>? onNonMessageValue = null) {
    if (result == null) {
      yield break;
    }

    // Handle single IMessage (includes IEvent and ICommand)
    if (result is IMessage singleMessage) {
      yield return singleMessage;
      yield break;
    }

    // Handle typed enumerables (IEnumerable<IMessage>, IEnumerable<IEvent>, IEnumerable<ICommand>)
    if (_tryExtractFromTypedEnumerable(result, out var typedMessages)) {
      foreach (var msg in typedMessages) {
        yield return msg;
      }
      yield break;
    }

    // Handle ValueTuple types (ITuple interface)
    if (result is ITuple tuple) {
      foreach (var msg in _extractFromTuple(tuple)) {
        yield return msg;
      }
      yield break;
    }

    // Handle general IEnumerable (for nested structures)
    if (result is IEnumerable enumerable and not string) {
      foreach (var msg in _extractFromGeneralEnumerable(enumerable)) {
        yield return msg;
      }
      yield break;
    }

    // Non-message, non-enumerable value - ignore
    // Log at error level so developers know their receptor returned something unexpected
    onNonMessageValue?.Invoke(result.GetType());
  }

  private static bool _tryExtractFromTypedEnumerable(object result, out IEnumerable<IMessage> messages) {
    if (result is IEnumerable<IMessage> messageEnumerable) {
      messages = messageEnumerable;
      return true;
    }

    if (result is IEnumerable<IEvent> eventEnumerable) {
      messages = eventEnumerable.Cast<IMessage>();
      return true;
    }

    if (result is IEnumerable<ICommand> commandEnumerable) {
      messages = commandEnumerable.Cast<IMessage>();
      return true;
    }

    messages = [];
    return false;
  }

  private static IEnumerable<IMessage> _extractFromTuple(ITuple tuple) {
    for (int i = 0; i < tuple.Length; i++) {
      var item = tuple[i];
      if (item != null) {
        foreach (var msg in ExtractMessages(item)) {
          yield return msg;
        }
      }
    }
  }

  private static IEnumerable<IMessage> _extractFromGeneralEnumerable(IEnumerable enumerable) {
    foreach (var item in enumerable) {
      if (item != null) {
        foreach (var msg in ExtractMessages(item)) {
          yield return msg;
        }
      }
    }
  }

  /// <summary>
  /// Extracts all IMessage instances with their resolved dispatch routing.
  /// Applies priority resolution: Message attribute > Receptor default > Individual wrapper > Collection wrapper > System default.
  /// </summary>
  /// <param name="result">The result to extract messages from</param>
  /// <param name="receptorDefault">Optional default routing from receptor's [DefaultRouting] attribute</param>
  /// <returns>Flattened collection of messages with their resolved dispatch modes</returns>
  /// <docs>fundamentals/dispatcher/dispatcher#routed-message-cascading</docs>
  /// <tests>tests/Whizbang.Core.Tests/Internal/MessageExtractorRoutingTests.cs</tests>
  public static IEnumerable<(IMessage Message, DispatchModes Mode)> ExtractMessagesWithRouting(
    object? result,
    DispatchModes? receptorDefault = null,
    Action<Type>? onNonMessageValue = null) {
    return _extractMessagesWithRoutingInternal(result, receptorDefault, null, null, onNonMessageValue);
  }

  /// <summary>
  /// Internal recursive implementation that tracks wrapper modes.
  /// </summary>
  private static IEnumerable<(IMessage Message, DispatchModes Mode)> _extractMessagesWithRoutingInternal(
    object? result,
    DispatchModes? receptorDefault,
    DispatchModes? individualWrapperMode,
    DispatchModes? collectionWrapperMode,
    Action<Type>? onNonMessageValue = null) {
    if (result == null) {
      yield break;
    }

    // Handle Routed<T> wrapper
    if (result is IRouted routed) {
      foreach (var item in _extractFromRouted(routed, receptorDefault, individualWrapperMode, collectionWrapperMode, onNonMessageValue)) {
        yield return item;
      }
      yield break;
    }

    // Handle single IMessage
    if (result is IMessage message) {
      yield return (message, _resolveMode(message, receptorDefault, individualWrapperMode, collectionWrapperMode));
      yield break;
    }

    // Handle typed enumerables
    foreach (var item in _extractFromEnumerable(result, receptorDefault, individualWrapperMode, collectionWrapperMode, onNonMessageValue)) {
      yield return item;
    }
  }

  private static IEnumerable<(IMessage Message, DispatchModes Mode)> _extractFromRouted(
    IRouted routed,
    DispatchModes? receptorDefault,
    DispatchModes? individualWrapperMode,
    DispatchModes? collectionWrapperMode,
    Action<Type>? onNonMessageValue) {
    // Skip RoutedNone values (discriminated union "no value" marker)
    if (routed.Mode == DispatchModes.None) {
      yield break;
    }

    var wrapperMode = routed.Mode;
    var innerValue = routed.Value;

    // Determine if this wraps an individual message or a collection
    var nextIndividual = innerValue is IMessage ? wrapperMode : individualWrapperMode;
    var nextCollection = innerValue is IMessage ? collectionWrapperMode : wrapperMode;

    foreach (var item in _extractMessagesWithRoutingInternal(
      innerValue, receptorDefault, nextIndividual, nextCollection, onNonMessageValue)) {
      yield return item;
    }
  }

  private static IEnumerable<(IMessage Message, DispatchModes Mode)> _extractFromEnumerable(
    object result,
    DispatchModes? receptorDefault,
    DispatchModes? individualWrapperMode,
    DispatchModes? collectionWrapperMode,
    Action<Type>? onNonMessageValue) {
    // Handle typed enumerables (IEnumerable<IMessage>, IEnumerable<IEvent>, IEnumerable<ICommand>)
    if (_tryExtractFromTypedEnumerable(result, out var typedMessages)) {
      foreach (var msg in typedMessages) {
        yield return (msg, _resolveMode(msg, receptorDefault, individualWrapperMode, collectionWrapperMode));
      }
      yield break;
    }

    // Handle ValueTuple types (ITuple interface)
    if (result is ITuple tuple) {
      foreach (var extracted in _extractFromTupleWithRouting(tuple, receptorDefault, individualWrapperMode, collectionWrapperMode, onNonMessageValue)) {
        yield return extracted;
      }
      yield break;
    }

    // Handle general IEnumerable (for nested structures)
    if (result is IEnumerable enumerable and not string) {
      foreach (var extracted in _extractFromGeneralEnumerableWithRouting(enumerable, receptorDefault, individualWrapperMode, collectionWrapperMode, onNonMessageValue)) {
        yield return extracted;
      }
      yield break;
    }

    // Non-message, non-enumerable value - ignore
    onNonMessageValue?.Invoke(result.GetType());
  }

  private static IEnumerable<(IMessage Message, DispatchModes Mode)> _extractFromTupleWithRouting(
    ITuple tuple,
    DispatchModes? receptorDefault,
    DispatchModes? individualWrapperMode,
    DispatchModes? collectionWrapperMode,
    Action<Type>? onNonMessageValue) {
    for (int i = 0; i < tuple.Length; i++) {
      var item = tuple[i];
      if (item != null) {
        foreach (var extracted in _extractMessagesWithRoutingInternal(
          item, receptorDefault, individualWrapperMode, collectionWrapperMode, onNonMessageValue)) {
          yield return extracted;
        }
      }
    }
  }

  private static IEnumerable<(IMessage Message, DispatchModes Mode)> _extractFromGeneralEnumerableWithRouting(
    IEnumerable enumerable,
    DispatchModes? receptorDefault,
    DispatchModes? individualWrapperMode,
    DispatchModes? collectionWrapperMode,
    Action<Type>? onNonMessageValue) {
    foreach (var item in enumerable) {
      if (item != null) {
        foreach (var extracted in _extractMessagesWithRoutingInternal(
          item, receptorDefault, individualWrapperMode, collectionWrapperMode, onNonMessageValue)) {
          yield return extracted;
        }
      }
    }
  }

  /// <summary>
  /// Resolves the final dispatch mode using priority order:
  /// 1. Message type attribute (HIGHEST - policy enforcement)
  /// 2. Receptor attribute (policy)
  /// 3. Individual wrapper (explicit per-item)
  /// 4. Collection wrapper (convenience default)
  /// 5. System default: Outbox (LOWEST - enables cross-service delivery)
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method uses GetCustomAttribute which is not AOT compatible. A future enhancement
  /// will replace this with a source-generated static RoutingMetadata lookup table that provides
  /// message type defaults without reflection.
  /// </para>
  /// <para>
  /// The default is Outbox for cross-service delivery. Use Route.Local() when you want
  /// to restrict cascade to local receptors only.
  /// </para>
  /// </remarks>
  private static DispatchModes _resolveMode(
    IMessage message,
    DispatchModes? receptorDefault,
    DispatchModes? individualWrapperMode,
    DispatchModes? collectionWrapperMode) {
    // Priority 1: Message type attribute (HIGHEST - policy enforcement)
    // Uses reflection (not AOT compatible) - will be replaced by generated lookup
    var messageAttr = message.GetType().GetCustomAttribute<DefaultRoutingAttribute>();
    if (messageAttr != null) {
      return messageAttr.Mode;
    }

    // Priority 2: Receptor attribute (policy)
    if (receptorDefault.HasValue) {
      return receptorDefault.Value;
    }

    // Priority 3: Individual wrapper (explicit per-item)
    if (individualWrapperMode.HasValue) {
      return individualWrapperMode.Value;
    }

    // Priority 4: Collection wrapper (convenience default)
    if (collectionWrapperMode.HasValue) {
      return collectionWrapperMode.Value;
    }

    // Priority 5: System default (LOWEST)
    // Default to Outbox for cross-service delivery (per routed cascade design).
    // Use Route.Local() to restrict to local receptors only.
    return DispatchModes.Outbox;
  }
}
