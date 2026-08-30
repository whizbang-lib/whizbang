namespace Whizbang.Core.Lifecycle;

/// <summary>
/// The shipped default destruction hook: proceed, and observe nothing afterward.
/// </summary>
/// <remarks>
/// <para>
/// An application that registers no destruction hook wants destruction to proceed, which is
/// precisely what a null hook produced. Expressing that as a value rather than an absence keeps the
/// behavior identical while making the dependency impossible to drop at a construction site.
/// </para>
/// <para>
/// This is a safe inert default because "no hook" already had a correct meaning. That is the test
/// that separates this from a dependency like a schema-readiness gate, where a permissive stub
/// would assert an invariant nobody checked rather than decline to intervene.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/injectable-services</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamCloserTests.cs</tests>
public sealed class NoOpDestructionHook : IDestructionHook {

  /// <summary>A shared instance; the type is stateless.</summary>
  public static readonly NoOpDestructionHook Instance = new();

  /// <inheritdoc />
  public ValueTask<DestructionResult> OnBeforeDestructionAsync(
      DestructionContext context, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(DestructionResult.Proceed());

  /// <inheritdoc />
  public ValueTask OnAfterDestructionAsync(
      DestructionContext context, CancellationToken cancellationToken = default) =>
    ValueTask.CompletedTask;
}
