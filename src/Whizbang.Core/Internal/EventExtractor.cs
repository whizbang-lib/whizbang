using System.Collections;
using System.Runtime.CompilerServices;

namespace Whizbang.Core.Internal;

/// <summary>
/// Utility for extracting events from complex return types.
/// Handles single events, arrays, enumerables, tuples, and nested structures.
/// Used by Dispatcher to automatically capture events from receptor return values.
/// </summary>
public static class EventExtractor {
  /// <summary>
  /// Extracts all IEvent instances from a potentially complex return value.
  /// Supports: single IEvent, IEvent[], IEnumerable&lt;IEvent&gt;, tuples (Tuple and ValueTuple), and nested structures.
  /// Non-event values are ignored.
  /// </summary>
  /// <param name="result">The result to extract events from</param>
  /// <returns>Flattened collection of all events found</returns>
  public static IEnumerable<IEvent> ExtractEvents(object? result) {
    if (result == null) {
      yield break;
    }

    // Handle single IEvent
    if (result is IEvent singleEvent) {
      yield return singleEvent;
      yield break;
    }

    // Handle IEnumerable<IEvent> (includes arrays)
    if (result is IEnumerable<IEvent> eventEnumerable) {
      foreach (var evt in eventEnumerable) {
        yield return evt;
      }
      yield break;
    }

    // Handle ValueTuple types (ITuple interface)
    if (result is ITuple tuple) {
      for (int i = 0; i < tuple.Length; i++) {
        var item = tuple[i];
        if (item != null) {
          // Recursively extract events from tuple items
          foreach (var evt in ExtractEvents(item)) {
            yield return evt;
          }
        }
      }
      yield break;
    }

    // Handle general IEnumerable (for nested structures)
    if (result is IEnumerable enumerable and not string) {
      foreach (var item in enumerable) {
        if (item != null) {
          // Recursively extract events from enumerable items
          foreach (var evt in ExtractEvents(item)) {
            yield return evt;
          }
        }
      }
      yield break;
    }

    // Non-event, non-enumerable value - ignore
  }
}
