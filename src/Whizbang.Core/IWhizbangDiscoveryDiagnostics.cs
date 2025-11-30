using Microsoft.Extensions.Logging;

namespace Whizbang.Core;

/// <summary>
/// Interface for Whizbang source generator diagnostics.
/// All generators that produce diagnostic output should implement this interface
/// in their generated code to provide consistent diagnostic APIs.
/// </summary>
public interface IWhizbangDiscoveryDiagnostics {
  /// <summary>
  /// Logs diagnostic information about what was discovered during source generation.
  /// </summary>
  /// <param name="logger">Logger instance for output</param>
  void LogDiscoveryDiagnostics(ILogger logger);

  /// <summary>
  /// Gets the name of the generator that produced this diagnostic output.
  /// </summary>
  string GeneratorName { get; }

  /// <summary>
  /// Gets the timestamp when the code was generated.
  /// </summary>
  string GeneratedTimestamp { get; }

  /// <summary>
  /// Gets the total count of items discovered by this generator.
  /// </summary>
  int TotalDiscoveredCount { get; }
}
