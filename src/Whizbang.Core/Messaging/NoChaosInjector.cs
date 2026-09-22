namespace Whizbang.Core.Messaging;

/// <summary>
/// The shipped default: injects nothing, and says so.
/// </summary>
/// <remarks>
/// <para>
/// Production registers this, so no composition is missing an injector and no construction site can
/// drop one. It reports <see cref="IChaosInjector.IsInjecting"/> as false, which is what an
/// unregistered injector used to mean, so callers behave exactly as before.
/// </para>
/// <para>
/// This is the case that shows why an inert default cannot be applied mechanically. Here the null
/// carried meaning, and a stub that failed to carry the same meaning would have turned chaos
/// injection on in production rather than merely doing nothing.
/// </para>
/// </remarks>
/// <docs>operations/testing/chaos-injection</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ChaosInjectorDefaultTests.cs</tests>
public sealed class NoChaosInjector : IChaosInjector {

  /// <summary>A shared instance; the type is stateless.</summary>
  public static readonly NoChaosInjector Instance = new();

  /// <inheritdoc />
  public bool IsInjecting => false;

  /// <inheritdoc />
  public ValueTask BeforeCheckpointAsync(
      string checkpoint, object? payload, CancellationToken cancellationToken) =>
    ValueTask.CompletedTask;
}
