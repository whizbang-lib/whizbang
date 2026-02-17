using System.Collections;
using System.Runtime.CompilerServices;

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
}
