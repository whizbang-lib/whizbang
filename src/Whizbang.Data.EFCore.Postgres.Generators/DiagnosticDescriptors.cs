using Microsoft.CodeAnalysis;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Diagnostic descriptors for EF Core Postgres generators and analyzers.
/// Uses WHIZ8xx range to avoid conflicts with main Whizbang.Generators (WHIZ001-199).
/// </summary>
internal static class DiagnosticDescriptors {
  private const string CATEGORY = "Whizbang.EFCore";

  /// <summary>
  /// WHIZ810: Warning - Perspective model contains Dictionary property.
  /// EF Core's ComplexProperty().ToJson() does not support Dictionary types.
  /// </summary>
  public static readonly DiagnosticDescriptor PerspectiveModelDictionaryProperty = new(
      id: "WHIZ810",
      title: "Perspective model contains Dictionary property",
      messageFormat: "Property '{0}' on perspective model '{1}' uses Dictionary<{2}, {3}> which is not supported by EF Core's ComplexProperty().ToJson(). Use List<{4}> with Key/Value properties instead.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "EF Core 10's ComplexProperty().ToJson() throws NullReferenceException for Dictionary properties. Use List<T> with Key/Value properties (like ScopeExtension or AttributeEntry pattern) instead."
  );
}
