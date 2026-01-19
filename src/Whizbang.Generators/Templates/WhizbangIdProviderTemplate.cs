using System;

namespace __NAMESPACE__;

#region HEADER
// This region gets replaced with generated header + timestamp
#endregion

/// <summary>
/// AOT-compatible provider for generating __TYPE_NAME__ instances.
/// Uses the configured base IWhizbangIdProvider to generate underlying Guid values.
/// </summary>
public sealed class __TYPE_NAME__Provider : global::Whizbang.Core.IWhizbangIdProvider<__TYPE_NAME__> {
  private readonly global::Whizbang.Core.IWhizbangIdProvider _baseProvider;

  /// <summary>
  /// Creates a new __TYPE_NAME__Provider with the specified base provider.
  /// </summary>
  /// <param name="baseProvider">The base provider to use for Guid generation</param>
  /// <exception cref="ArgumentNullException">Thrown when baseProvider is null</exception>
  public __TYPE_NAME__Provider(global::Whizbang.Core.IWhizbangIdProvider baseProvider) {
    _baseProvider = baseProvider ?? throw new ArgumentNullException(nameof(baseProvider));
  }

  /// <summary>
  /// Creates a new __TYPE_NAME__ instance using the configured base provider.
  /// </summary>
  /// <returns>A new __TYPE_NAME__ instance with a unique value</returns>
  public __TYPE_NAME__ NewId() => __TYPE_NAME__.From(_baseProvider.NewGuid());
}
