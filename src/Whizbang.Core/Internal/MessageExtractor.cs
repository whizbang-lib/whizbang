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
/// <docs>core-concepts/dispatcher#automatic-message-cascade</docs>
public static class MessageExtractor {
  /// <summary>
  /// Extracts all IMessage instances (events and commands) from a potentially complex return value.
  /// Supports: single IMessage, IMessage[], IEnumerable&lt;IMessage&gt;, tuples (Tuple and ValueTuple), and nested structures.
  /// Non-message values are ignored.
  /// </summary>
  /// <param name="result">The result to extract messages from</param>
  /// <returns>Flattened collection of all messages found</returns>
  /// <docs>core-concepts/dispatcher#automatic-message-cascade</docs>
  /// <tests>tests/Whizbang.Core.Tests/Internal/MessageExtractorTests.cs</tests>
  public static IEnumerable<IMessage> ExtractMessages(object? result) {
    if (result == null) {
      yield break;
    }

    // Handle single IMessage (includes IEvent and ICommand)
    if (result is IMessage singleMessage) {
      yield return singleMessage;
      yield break;
    }

    // Handle IEnumerable<IMessage> (includes arrays of events/commands)
    if (result is IEnumerable<IMessage> messageEnumerable) {
      foreach (var msg in messageEnumerable) {
        yield return msg;
      }
      yield break;
    }

    // Handle IEnumerable<IEvent> (includes arrays)
    if (result is IEnumerable<IEvent> eventEnumerable) {
      foreach (var evt in eventEnumerable) {
        yield return evt;
      }
      yield break;
    }

    // Handle IEnumerable<ICommand> (includes arrays)
    if (result is IEnumerable<ICommand> commandEnumerable) {
      foreach (var cmd in commandEnumerable) {
        yield return cmd;
      }
      yield break;
    }

    // Handle ValueTuple types (ITuple interface)
    if (result is ITuple tuple) {
      for (int i = 0; i < tuple.Length; i++) {
        var item = tuple[i];
        if (item != null) {
          // Recursively extract messages from tuple items
          foreach (var msg in ExtractMessages(item)) {
            yield return msg;
          }
        }
      }
      yield break;
    }

    // Handle general IEnumerable (for nested structures)
    if (result is IEnumerable enumerable and not string) {
      foreach (var item in enumerable) {
        if (item != null) {
          // Recursively extract messages from enumerable items
          foreach (var msg in ExtractMessages(item)) {
            yield return msg;
          }
        }
      }
      yield break;
    }

    // Non-message, non-enumerable value - ignore
  }

  /// <summary>
  /// Extracts all IMessage instances with their resolved dispatch routing.
  /// Applies priority resolution: Message attribute > Receptor default > Individual wrapper > Collection wrapper > System default.
  /// </summary>
  /// <param name="result">The result to extract messages from</param>
  /// <param name="receptorDefault">Optional default routing from receptor's [DefaultRouting] attribute</param>
  /// <returns>Flattened collection of messages with their resolved dispatch modes</returns>
  /// <docs>core-concepts/dispatcher#routed-message-cascading</docs>
  /// <tests>tests/Whizbang.Core.Tests/Internal/MessageExtractorRoutingTests.cs</tests>
  public static IEnumerable<(IMessage Message, DispatchMode Mode)> ExtractMessagesWithRouting(
    object? result,
    DispatchMode? receptorDefault = null) {
    return _extractMessagesWithRoutingInternal(result, receptorDefault, null, null);
  }

  /// <summary>
  /// Internal recursive implementation that tracks wrapper modes.
  /// </summary>
  private static IEnumerable<(IMessage Message, DispatchMode Mode)> _extractMessagesWithRoutingInternal(
    object? result,
    DispatchMode? receptorDefault,
    DispatchMode? individualWrapperMode,
    DispatchMode? collectionWrapperMode) {
    if (result == null) {
      yield break;
    }

    // Handle Routed<T> wrapper
    if (result is IRouted routed) {
      var wrapperMode = routed.Mode;
      var innerValue = routed.Value;

      // Determine if this wraps an individual message or a collection
      if (innerValue is IMessage) {
        // Individual wrapper: Routed<SomeEvent>
        foreach (var item in _extractMessagesWithRoutingInternal(
          innerValue, receptorDefault,
          individualWrapperMode: wrapperMode,  // Pass as individual
          collectionWrapperMode))              // Preserve outer collection mode
        {
          yield return item;
        }
      } else {
        // Collection wrapper: Routed<IEvent[]> or Routed<(E1, E2)>
        foreach (var item in _extractMessagesWithRoutingInternal(
          innerValue, receptorDefault,
          individualWrapperMode,               // Preserve inner individual mode
          collectionWrapperMode: wrapperMode)) // Pass as collection
        {
          yield return item;
        }
      }
      yield break;
    }

    // Handle single IMessage
    if (result is IMessage message) {
      var mode = _resolveMode(message, receptorDefault, individualWrapperMode, collectionWrapperMode);
      yield return (message, mode);
      yield break;
    }

    // Handle IEnumerable<IMessage> (includes arrays of events/commands)
    if (result is IEnumerable<IMessage> messageEnumerable) {
      foreach (var msg in messageEnumerable) {
        var mode = _resolveMode(msg, receptorDefault, individualWrapperMode, collectionWrapperMode);
        yield return (msg, mode);
      }
      yield break;
    }

    // Handle IEnumerable<IEvent> (includes arrays)
    if (result is IEnumerable<IEvent> eventEnumerable) {
      foreach (var evt in eventEnumerable) {
        var mode = _resolveMode(evt, receptorDefault, individualWrapperMode, collectionWrapperMode);
        yield return (evt, mode);
      }
      yield break;
    }

    // Handle IEnumerable<ICommand> (includes arrays)
    if (result is IEnumerable<ICommand> commandEnumerable) {
      foreach (var cmd in commandEnumerable) {
        var mode = _resolveMode(cmd, receptorDefault, individualWrapperMode, collectionWrapperMode);
        yield return (cmd, mode);
      }
      yield break;
    }

    // Handle ValueTuple types (ITuple interface)
    if (result is ITuple tuple) {
      for (int i = 0; i < tuple.Length; i++) {
        var item = tuple[i];
        if (item != null) {
          // Recursively extract messages from tuple items
          foreach (var extracted in _extractMessagesWithRoutingInternal(
            item, receptorDefault, individualWrapperMode, collectionWrapperMode)) {
            yield return extracted;
          }
        }
      }
      yield break;
    }

    // Handle general IEnumerable (for nested structures)
    if (result is IEnumerable enumerable and not string) {
      foreach (var item in enumerable) {
        if (item != null) {
          // Recursively extract messages from enumerable items
          foreach (var extracted in _extractMessagesWithRoutingInternal(
            item, receptorDefault, individualWrapperMode, collectionWrapperMode)) {
            yield return extracted;
          }
        }
      }
      yield break;
    }

    // Non-message, non-enumerable value - ignore
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
  /// TODO: Replace GetCustomAttribute call with generated lookup table for AOT compatibility.
  /// The source generator should generate a static RoutingMetadata class that provides
  /// message type defaults without reflection.
  /// </para>
  /// <para>
  /// The default is Outbox for cross-service delivery. Use Route.Local() when you want
  /// to restrict cascade to local receptors only.
  /// </para>
  /// </remarks>
  private static DispatchMode _resolveMode(
    IMessage message,
    DispatchMode? receptorDefault,
    DispatchMode? individualWrapperMode,
    DispatchMode? collectionWrapperMode) {
    // Priority 1: Message type attribute (HIGHEST - policy enforcement)
    // TODO: Replace with generated lookup for AOT compatibility
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
    return DispatchMode.Outbox;
  }
}
