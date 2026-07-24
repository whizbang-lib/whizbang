using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default <see cref="IRawReceptorRegistry"/> implementation populated from DI. Resolves all
/// registered <see cref="IRawReceptor"/> instances and indexes them by
/// <see cref="IRawReceptor.TargetMessageTypeName"/> (with the same normalization the typed
/// JsonContextRegistry uses, so version drift doesn't break lookup).
/// </summary>
/// <docs>fundamentals/receptors/raw-receptors</docs>
public sealed class RawReceptorRegistry : IRawReceptorRegistry {
  private readonly Dictionary<string, IRawReceptor> _byNormalizedName;

  /// <summary>
  /// Constructs the registry from DI-supplied raw receptors. Multiple receptors registering
  /// the same target type name throws — at most one raw receptor per type.
  /// </summary>
  public RawReceptorRegistry(IEnumerable<IRawReceptor> receptors) {
    ArgumentNullException.ThrowIfNull(receptors);

    var byName = new Dictionary<string, IRawReceptor>(StringComparer.Ordinal);
    foreach (var receptor in receptors) {
      var normalized = EventTypeMatchingHelper.NormalizeTypeName(receptor.TargetMessageTypeName);
      if (byName.TryGetValue(normalized, out var existing)) {
        throw new InvalidOperationException(
          $"Multiple IRawReceptor implementations registered for type name '{normalized}': " +
          $"'{existing.GetType().FullName}' and '{receptor.GetType().FullName}'. At most one raw receptor per type is allowed.");
      }
      byName[normalized] = receptor;
    }

    _byNormalizedName = byName;
  }

  /// <inheritdoc />
  public IRawReceptor? FindByTypeName(string messageTypeName) {
    if (string.IsNullOrEmpty(messageTypeName)) {
      return null;
    }
    var normalized = EventTypeMatchingHelper.NormalizeTypeName(messageTypeName);
    return _byNormalizedName.TryGetValue(normalized, out var receptor) ? receptor : null;
  }

  /// <inheritdoc />
  public IReadOnlyCollection<string> RegisteredTypeNames => [.. _byNormalizedName.Keys];
}
