using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Whizbang.Testing.Options;

/// <summary>
/// An <see cref="IOptionsMonitor{TOptions}"/> over one fixed value, for tests and hosts that construct a
/// framework type directly instead of resolving it from a container. The framework's workers take their
/// monitors as required dependencies, so a hand-built worker needs an instance; this one never changes
/// and never fires a change notification.
/// </summary>
/// <typeparam name="TOptions">The options type.</typeparam>
/// <docs>extending/extensibility/replaceable-services</docs>
public sealed class StaticOptionsMonitor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(TOptions value)
    : IOptionsMonitor<TOptions> where TOptions : class {
  private readonly TOptions _value = value ?? throw new ArgumentNullException(nameof(value));

  /// <inheritdoc />
  public TOptions CurrentValue => _value;

  /// <inheritdoc />
  public TOptions Get(string? name) => _value;

  /// <inheritdoc />
  public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
}
