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

  /// <summary>
  /// Gets information about all registered perspectives (zero reflection).
  /// Useful for diagnostic messages when runner lookup fails.
  /// </summary>
  /// <returns>Collection of registered perspective information with type details</returns>
  IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives();
}

/// <summary>
/// Information about a registered perspective for diagnostic purposes.
/// </summary>
/// <param name="ClrTypeName">CLR format type name used for lookup (e.g., "MyApp.Perspectives.OrderPerspective" or "MyApp.Parent+Nested")</param>
/// <param name="FullyQualifiedName">Fully qualified type name for code generation (e.g., "global::MyApp.Perspectives.OrderPerspective")</param>
/// <param name="ModelType">Fully qualified model type (e.g., "global::MyApp.Models.OrderModel")</param>
/// <param name="EventTypes">Fully qualified event types handled by this perspective</param>
public sealed record PerspectiveRegistrationInfo(
    string ClrTypeName,
    string FullyQualifiedName,
    string ModelType,
    IReadOnlyList<string> EventTypes
);
