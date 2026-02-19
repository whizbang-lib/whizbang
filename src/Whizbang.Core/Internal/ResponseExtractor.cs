using System.Collections;
using System.Runtime.CompilerServices;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Internal;

/// <summary>
/// Extracts a typed response from complex receptor return values.
/// Used for RPC-style LocalInvokeAsync calls where the caller requests a specific type
/// from a receptor that returns a tuple or complex result.
/// Supports: single value, tuples (ValueTuple/ITuple), arrays, enumerables, and Routed&lt;T&gt; wrappers.
/// AOT-compatible: uses ITuple interface and pattern matching, no reflection.
/// </summary>
/// <docs>core-concepts/rpc-extraction</docs>
/// <tests>Whizbang.Core.Tests/Internal/ResponseExtractorTests.cs</tests>
public static class ResponseExtractor {
  /// <summary>
  /// Tries to extract a value of type TResponse from a potentially complex return value.
  /// Returns true if found, false otherwise.
  /// Supports: single value, tuples (ValueTuple/ITuple), arrays, enumerables, and Routed&lt;T&gt; wrappers.
  /// Returns the first matching value found (for ReferenceEquals comparison in cascade exclusion).
  /// </summary>
  /// <remarks>
  /// When extracting from Routed&lt;T&gt; wrappers, the inner value is extracted regardless
  /// of the routing mode. This ensures RPC responses return to the caller even if
  /// wrapped in Route.Local(), Route.Outbox(), or Route.Both().
  /// </remarks>
  /// <typeparam name="TResponse">The type to extract from the result</typeparam>
  /// <param name="result">The complex result to extract from</param>
  /// <param name="response">The extracted value, or default if not found</param>
  /// <returns>True if a value of type TResponse was found and extracted</returns>
  public static bool TryExtractResponse<TResponse>(object? result, out TResponse? response) {
    if (result == null) {
      response = default;
      return false;
    }

    // Handle Routed<T> wrapper - unwrap and search inner value
    // Skip RoutedNone values (discriminated union "no value" marker)
    if (result is IRouted routed) {
      if (routed.Mode == DispatchMode.None) {
        response = default;
        return false;
      }
      return TryExtractResponse(routed.Value, out response);
    }

    // Handle direct match
    if (result is TResponse directMatch) {
      response = directMatch;
      return true;
    }

    // Handle ValueTuple types (ITuple interface) - AOT-compatible
    if (result is ITuple tuple) {
      for (int i = 0; i < tuple.Length; i++) {
        var item = tuple[i];
        if (item != null && TryExtractResponse(item, out response)) {
          return true;
        }
      }
      response = default;
      return false;
    }

    // Handle general IEnumerable (arrays, lists, etc.) but not string
    if (result is IEnumerable enumerable and not string) {
      foreach (var item in enumerable) {
        if (item != null && TryExtractResponse(item, out response)) {
          return true;
        }
      }
      response = default;
      return false;
    }

    // No match found
    response = default;
    return false;
  }
}
