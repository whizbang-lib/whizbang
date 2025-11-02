using Microsoft.CodeAnalysis;

namespace Whizbang.Generators;

/// <summary>
/// Generates the central diagnostics infrastructure for collecting
/// and displaying build-time diagnostic information from all generators.
/// </summary>
[Generator]
public class DiagnosticsGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Register a single output that generates the diagnostics infrastructure
    context.RegisterPostInitializationOutput(ctx => {
      ctx.AddSource("WhizbangDiagnostics.g.cs", GenerateDiagnosticsInfrastructure());
    });
  }

  private static string GenerateDiagnosticsInfrastructure() {
    // Load template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(DiagnosticsGenerator).Assembly,
        "WhizbangDiagnosticsTemplate.cs"
    );

    // Replace header with timestamp
    return TemplateUtilities.ReplaceHeaderRegion(typeof(DiagnosticsGenerator).Assembly, template);
  }
}
