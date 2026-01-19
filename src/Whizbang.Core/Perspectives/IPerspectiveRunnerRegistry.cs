namespace Whizbang.Core.Perspectives;

/// <summary>
/// Zero-reflection registry for perspective runner lookup (AOT-compatible).
/// Implemented by source-generated PerspectiveRunnerRegistry in {AssemblyName}.Generated namespace.
/// </summary>
public interface IPerspectiveRunnerRegistry {
  /// <summary>
  /// Gets a perspective runner by perspective type name (zero reflection).
  /// Returns null if no runner found for the given perspective name.
  /// </summary>
  /// <param name="perspectiveName">Simple name of the perspective class (e.g., "InventoryLevelsPerspective")</param>
  /// <param name="serviceProvider">Service provider to resolve runner dependencies</param>
  /// <returns>IPerspectiveRunner instance or null if not found</returns>
  IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider);
}
