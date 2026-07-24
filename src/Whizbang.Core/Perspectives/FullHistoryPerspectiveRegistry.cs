using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Runtime set of perspective names marked <see cref="Attributes.FullHistoryAttribute"/> — full-history
/// projections that cannot resume from a carry-forward / closing event. Populated at startup by generated
/// <c>[ModuleInitializer]</c> code (the perspective-runner generator emits a registration for each
/// <c>[FullHistory]</c> perspective, keyed by the same name its message associations / cursors use). A1's
/// <see cref="Whizbang.Core.Lifecycle.IStreamCloser"/> consults it to refuse a discard-close of a stream any
/// full-history projection consumes.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public static class FullHistoryPerspectiveRegistry {
  private static readonly ConcurrentDictionary<string, bool> _names = new(StringComparer.Ordinal);

  /// <summary>Registers a perspective name as full-history. Idempotent. Called from generated module
  /// initializers.</summary>
  /// <param name="perspectiveName">The perspective's name (matching its association <c>target_name</c>).</param>
  public static void Register(string perspectiveName) {
    ArgumentException.ThrowIfNullOrEmpty(perspectiveName);
    _names[perspectiveName] = true;
  }

  /// <summary>Whether <paramref name="perspectiveName"/> is a registered full-history projection.</summary>
  public static bool IsFullHistory(string perspectiveName) =>
    !string.IsNullOrEmpty(perspectiveName) && _names.ContainsKey(perspectiveName);

  /// <summary>Whether any registered perspective name in <paramref name="perspectiveNames"/> is full-history.</summary>
  public static bool AnyFullHistory(IEnumerable<string> perspectiveNames) {
    ArgumentNullException.ThrowIfNull(perspectiveNames);
    foreach (var name in perspectiveNames) {
      if (IsFullHistory(name)) {
        return true;
      }
    }
    return false;
  }
}
