using Microsoft.CodeAnalysis;

namespace Whizbang.Generators;

/// <summary>
/// Diagnostic descriptors for the Whizbang source generator.
/// </summary>
internal static class DiagnosticDescriptors {
  private const string CATEGORY = "Whizbang.SourceGeneration";

  /// <summary>
  /// WHIZ001: Info - Receptor discovered during source generation.
  /// </summary>
  public static readonly DiagnosticDescriptor ReceptorDiscovered = new(
      id: "WHIZ001",
      title: "Receptor Discovered",
      messageFormat: "Found receptor '{0}' handling {1} → {2}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "A receptor implementation was discovered and will be registered."
  );

  /// <summary>
  /// WHIZ002: Warning - No receptors found in the compilation.
  /// </summary>
  public static readonly DiagnosticDescriptor NoReceptorsFound = new(
      id: "WHIZ002",
      title: "No Receptors Found",
      messageFormat: "No IReceptor implementations were found in the compilation",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "The source generator did not find any classes implementing IReceptor<TMessage, TResponse>."
  );

  /// <summary>
  /// WHIZ003: Error - Invalid receptor implementation detected.
  /// </summary>
  public static readonly DiagnosticDescriptor InvalidReceptor = new(
      id: "WHIZ003",
      title: "Invalid Receptor Implementation",
      messageFormat: "Invalid receptor '{0}': {1}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The receptor implementation has errors and cannot be registered."
  );
}
