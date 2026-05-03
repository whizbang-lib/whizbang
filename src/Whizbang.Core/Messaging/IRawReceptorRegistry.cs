using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Registry that maps <c>message_type</c> CLR name strings to opt-in
/// <see cref="IRawReceptor"/> implementations. The dispatcher consults this registry after
/// the typed binder cascade misses, so messages whose CLR type doesn't resolve locally can
/// still be handled by a service that opts into raw JSON dispatch.
/// </summary>
/// <remarks>
/// Slice 5 of the resilient-transport plan ships interface + manual-registration impl. A
/// future source-generator extension (<c>ReceptorDiscoveryGenerator</c>) will populate the
/// registry at compile time alongside typed receptors. Today applications register raw
/// receptors via DI manually:
/// <code>
/// services.AddSingleton&lt;IRawReceptor, MyRawReceptor&gt;();
/// services.AddSingleton&lt;IRawReceptorRegistry, RawReceptorRegistry&gt;();
/// </code>
/// </remarks>
/// <docs>fundamentals/receptors/raw-receptors</docs>
public interface IRawReceptorRegistry {
  /// <summary>
  /// Returns the raw receptor whose <see cref="IRawReceptor.TargetMessageTypeName"/> matches
  /// <paramref name="messageTypeName"/>, or null when no raw receptor is registered for the
  /// type. String comparison uses ordinal equality with optional version-stripping via
  /// <see cref="EventTypeMatchingHelper.NormalizeTypeName"/>.
  /// </summary>
  IRawReceptor? FindByTypeName(string messageTypeName);

  /// <summary>
  /// Returns the type names this registry can dispatch (the union of registered raw receptors).
  /// Useful for diagnostic logs and the slice-2 receptor-registry filter at receive — a
  /// message destined for a registered raw receptor must NOT be dropped by that filter.
  /// </summary>
  IReadOnlyCollection<string> RegisteredTypeNames { get; }
}
