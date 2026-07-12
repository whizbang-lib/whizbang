using System.Collections.Concurrent;

namespace Whizbang.Core.Signals;

/// <summary>
/// Static, cross-assembly registry of <see cref="ISignal"/> type metadata. Each assembly's generated
/// <see cref="ISignalTypeSource"/> self-registers here via a module initializer (before <c>Main</c>),
/// so the running host reads the combined union across the whole dependency chain. Mirrors
/// <c>EventNamespaceRegistry</c> / <c>SyncEventTypeRegistrations</c>.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public static class SignalTypeRegistry {
  private static readonly ConcurrentBag<ISignalTypeSource> _sources = [];

  /// <summary>Register an assembly's signal-type source (called from a generated module initializer).</summary>
  public static void Register(ISignalTypeSource source) {
    ArgumentNullException.ThrowIfNull(source);
    _sources.Add(source);
  }

  /// <summary>The combined union of signal-type metadata across every registered source.</summary>
  public static IReadOnlyList<SignalTypeEntry> GetAll() {
    var combined = new List<SignalTypeEntry>();
    foreach (var source in _sources) {
      combined.AddRange(source.GetSignalTypes());
    }
    return combined;
  }

  /// <summary>Number of registered sources (diagnostics/testing).</summary>
  public static int RegisteredCount => _sources.Count;

  /// <summary>Test-only reset of the static registration state.</summary>
  internal static void Clear() => _sources.Clear();
}
