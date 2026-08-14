using System;
using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Runtime map of a perspective model type to its row TTL (seconds), for
/// <see cref="Attributes.TransientStorage.TtlRow"/> perspectives. Populated at startup by generated
/// <c>[ModuleInitializer]</c> code — the perspective-runner generator resolves a perspective's effective
/// storage + TTL <em>virally</em> from its <c>[Ephemeral]</c> events. The EF Core perspective upsert consults
/// it to stamp <c>PerspectiveRow.ExpiresAt = now + ttl</c> (a sliding last-activity window). A model with no
/// registration (PersistedRow / InMemory / Sourced) resolves to <c>-1</c> and its rows never expire.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public static class PerspectiveTtlRegistry {
  private static readonly ConcurrentDictionary<Type, int> _ttlSecondsByModel = new();

  /// <summary>
  /// Registers a <see cref="Attributes.TransientStorage.TtlRow"/> perspective's model type with its row TTL
  /// in seconds. Idempotent (last registration wins). Called from generated module initializers.
  /// </summary>
  /// <param name="modelType">The perspective's read-model CLR type.</param>
  /// <param name="ttlSeconds">The row TTL in seconds (the applied AfterTtl event's window).</param>
  public static void Register(Type modelType, int ttlSeconds) {
    ArgumentNullException.ThrowIfNull(modelType);
    _ttlSecondsByModel[modelType] = ttlSeconds;
  }

  private static volatile bool _enabled = true;
  private static volatile Dictionary<string, int?>? _runtimeOverrides;

  /// <summary>
  /// Applies the operator rung of the row-retention override ladder (perspective row retention).
  /// <paramref name="enabled"/> is the global kill switch — when false every model resolves to
  /// <c>-1</c>, so stamping, the lens expiry filter, and the resurrection probe all stand down
  /// together (one consult point keeps the seams coherent). <paramref name="overrides"/> maps a
  /// model's full CLR name to a replacement TTL in seconds (or <c>null</c> to disable retention
  /// for that model only); an override outranks the generated registration. Called at startup by
  /// the worker pipeline from <c>PerspectiveRowRetentionOptions</c>; passing
  /// <c>(true, null)</c> restores pure registered behavior.
  /// </summary>
  /// <docs>fundamentals/perspectives/row-retention</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveTtlRegistryTests.cs</tests>
  public static void ApplyRuntimeConfiguration(bool enabled, IReadOnlyDictionary<string, int?>? overrides) {
    _enabled = enabled;
    _runtimeOverrides = overrides is null or { Count: 0 }
      ? null
      : new Dictionary<string, int?>(overrides, StringComparer.Ordinal);
  }

  /// <summary>
  /// The effective row TTL (seconds) for <paramref name="modelType"/>, or <c>-1</c> when the model's rows
  /// never expire. Resolution order: kill switch → runtime override (by full CLR name) → the generated
  /// registration → <c>-1</c>.
  /// </summary>
  /// <param name="modelType">The perspective's read-model CLR type.</param>
  public static int ResolveSeconds(Type modelType) {
    if (modelType is null || !_enabled) {
      return -1;
    }
    var overrides = _runtimeOverrides;
    if (overrides is not null && modelType.FullName is { } fullName
        && overrides.TryGetValue(fullName, out var overridden)) {
      return overridden ?? -1;
    }
    return _ttlSecondsByModel.TryGetValue(modelType, out var seconds) ? seconds : -1;
  }
}
