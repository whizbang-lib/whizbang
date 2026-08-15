using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Process-wide map of perspective model type to its declared row cap, populated from generated
/// <c>[ModuleInitializer]</c> code exactly as <see cref="PerspectiveTtlRegistry"/> is.
/// </summary>
/// <remarks>
/// Self-registration rather than assembly scanning is an AOT requirement, not a style choice:
/// discovering perspectives by scanning needs reflection, which is precisely what the
/// source-generated registration pattern exists to avoid.
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
public static class PerspectiveRowCapRegistry {
  private static readonly ConcurrentDictionary<Type, RowCapRegistration> _capsByModel = new();

  /// <summary>Registers a perspective's declared cap. Idempotent; last registration wins.</summary>
  public static void Register(Type modelType, int cap, string? scopeKey) {
    ArgumentNullException.ThrowIfNull(modelType);
    _capsByModel[modelType] = new RowCapRegistration(cap, scopeKey);
  }

  /// <summary>The declared cap for a model, or null when it declares none.</summary>
  public static RowCapRegistration? Resolve(Type modelType) =>
    modelType is not null && _capsByModel.TryGetValue(modelType, out var cap) && cap.Cap >= 0
      ? cap
      : null;

  /// <summary>
  /// A snapshot of every model registered across the loaded assemblies, for the startup sync.
  /// </summary>
  public static IReadOnlyList<KeyValuePair<Type, RowCapRegistration>> RegisteredModels() =>
    [.. _capsByModel];
}

/// <summary>One perspective's declared cap and the scope key partitioning its ranking.</summary>
/// <param name="Cap">Maximum rows retained per partition; negative means unbounded.</param>
/// <param name="ScopeKey">Scope JSON key partitioning the ranking ('u', 't'), or null for whole-table.</param>
public readonly record struct RowCapRegistration(int Cap, string? ScopeKey);
