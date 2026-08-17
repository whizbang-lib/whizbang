using System.Reflection;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Resolves the current "generation" tag used by the DLQ recovery subsystem to mark
/// dead-lettered rows for deploy-aware auto-replay.
/// </summary>
/// <remarks>
/// <para>
/// When the recovery worker scans on startup, any DLQ row whose <c>generation</c> is NOT
/// in its <c>retried_on_generations</c> array gets auto-retried exactly once. The intent:
/// on every new deploy, give DLQ rows from prior generations one shot at the new code
/// path. Catches "we shipped a fix" cases without operator intervention.
/// </para>
/// <para>
/// The default implementation returns the Whizbang.Core assembly version. Real
/// deployments should register their own implementation that combines the Whizbang
/// version with the service's own version (and optionally a branch / commit hash) so
/// distinct rebuilds of the same Whizbang version produce distinct generation strings.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultGenerationProviderTests.cs:GetGeneration_ReturnsNonEmptyStringAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultGenerationProviderTests.cs:GetGeneration_StableAcrossInstancesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerDeadLetterTests.cs:CustomGenerationProvider_OverridesDefaultWhenRegisteredAsync</tests>
public interface IGenerationProvider {
  /// <summary>
  /// Returns the current generation tag — a short stable string that uniquely identifies
  /// the running build. Must be deterministic across the process lifetime (don't compute
  /// fresh each call; the recovery worker compares strings exactly).
  /// </summary>
  string GetGeneration();
}

/// <summary>
/// Default <see cref="IGenerationProvider"/>: returns the Whizbang.Core assembly version
/// as a string. Adequate for development; production deployments should register a custom
/// implementation that includes the service's own version.
/// </summary>
public sealed class DefaultGenerationProvider : IGenerationProvider {
  private readonly string _value;

  /// <summary>Initializes the provider, capturing the version once at construction.</summary>
  public DefaultGenerationProvider() {
    var version = typeof(DefaultGenerationProvider).Assembly
      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
      ?? typeof(DefaultGenerationProvider).Assembly.GetName().Version?.ToString()
      ?? "unknown";
    _value = $"whizbang/{version}";
  }

  /// <inheritdoc />
  public string GetGeneration() => _value;
}
