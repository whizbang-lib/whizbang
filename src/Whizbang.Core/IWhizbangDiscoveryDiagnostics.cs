using Microsoft.Extensions.Logging;

namespace Whizbang.Core;

/// <summary>
/// Interface for Whizbang source generator diagnostics.
/// All generators that produce diagnostic output should implement this interface
/// in their generated code to provide consistent diagnostic APIs.
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedCode_ImplementsIDiagnosticsInterfaceAsync</tests>
public interface IWhizbangDiscoveryDiagnostics {
  /// <summary>
  /// Logs diagnostic information about what was discovered during source generation.
  /// </summary>
  /// <param name="logger">Logger instance for output</param>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:LogDiscoveryDiagnostics_OutputsPerspectiveDetailsAsync</tests>
  void LogDiscoveryDiagnostics(ILogger logger);

  /// <summary>
  /// Gets the name of the generator that produced this diagnostic output.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_HasCorrectGeneratorNameAsync</tests>
  string GeneratorName { get; }

  /// <summary>
  /// Gets the timestamp when the code was generated.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedCode_ImplementsIDiagnosticsInterfaceAsync</tests>
  string GeneratedTimestamp { get; }

  /// <summary>
  /// Gets the total count of items discovered by this generator.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_ReportsCorrectPerspectiveCountAsync</tests>
  int TotalDiscoveredCount { get; }
}
