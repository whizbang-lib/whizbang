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

  /// <summary>
  /// The registered row TTL (seconds) for <paramref name="modelType"/>, or <c>-1</c> when the model is not a
  /// TtlRow perspective (its rows never expire).
  /// </summary>
  /// <param name="modelType">The perspective's read-model CLR type.</param>
  public static int ResolveSeconds(Type modelType) =>
    modelType is not null && _ttlSecondsByModel.TryGetValue(modelType, out var seconds) ? seconds : -1;
}
